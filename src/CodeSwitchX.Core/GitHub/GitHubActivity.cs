using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Core.GitHub;

/// <summary>
/// The open issues and pull requests of the repositories the Yard's folders belong to, in memory only. A round reads the
/// accounts once and each repository once however many folders share it, four at a time; a read that fails keeps the last
/// result, one every account answers with "not found" drops it, and a result that did not change keeps its instance, so the
/// tiles built on it compare equal and stay as they are. A round raises <see cref="Changed"/> when a result changed or a folder
/// now belongs to another repository. <see cref="Retain"/> lets go of what no folder on the tiles needs any more.
/// </summary>
public sealed class GitHubActivity(IGhCli gh, GitHubReader reader, Func<string, CancellationToken, Task<OriginLookup>> originOf, ILogger<GitHubActivity> logger)
{
    private const int MostReadsAtOnce = 4;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _round = new(1, 1);
    private readonly SemaphoreSlim _reads = new(MostReadsAtOnce, MostReadsAtOnce);
    private readonly Dictionary<string, GitHubRepo?> _repoOfFolder = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<GitHubRepo, RepoActivity> _results = [];

    /// <summary>A round changed at least one result or moved a folder to another repository (its first included), or <see cref="Replace"/> changed a result; raised on whichever thread did it.</summary>
    public event EventHandler? Changed;

    /// <summary>What was last read for the repository the folder belongs to; null before the first read, when it has no GitHub origin, or when no account can see the repository.</summary>
    public RepoActivity? ForFolder(string path)
    {
        lock (_gate)
        {
            return _repoOfFolder.GetValueOrDefault(Key(path)) is { } repo ? _results.GetValueOrDefault(repo) : null;
        }
    }

    /// <summary>The result last stored for the repository, or null; take it before a long read and give it to <see cref="Replace"/>.</summary>
    public RepoActivity? Latest(GitHubRepo repo)
    {
        lock (_gate)
        {
            return _results.GetValueOrDefault(repo);
        }
    }

    /// <summary>Whether a round has looked at the folder's origin, whatever it found.</summary>
    public bool Knows(string path)
    {
        lock (_gate)
        {
            return _repoOfFolder.ContainsKey(Key(path));
        }
    }

    /// <summary>
    /// Keeps the entry of each of <paramref name="folders"/> and drops the others, and drops the result of every repository no remaining
    /// folder refers to: a folder that left the tiles, or the workspace file's list, or whose origin moved, is read afresh when it comes back.
    /// </summary>
    public void Retain(IReadOnlyCollection<string> folders)
    {
        var keep = folders.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var folder in _repoOfFolder.Keys.Where(f => !keep.Contains(f)).ToList())
            {
                _repoOfFolder.Remove(folder);
            }

            var referred = _repoOfFolder.Values.OfType<GitHubRepo>().ToHashSet();
            foreach (var repo in _results.Keys.Where(r => !referred.Contains(r)).ToList())
            {
                _results.Remove(repo);
            }
        }
    }

    /// <summary>Works out the repository of each folder, reads each repository once and raises <see cref="Changed"/> once at the end if a result differs.</summary>
    public async Task RefreshAsync(IReadOnlyCollection<string> folders, CancellationToken ct)
    {
        await _round.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var folderRepos = await Task.WhenAll(folders.Select(f => Key(f)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(folder => RepoOfAsync(folder, ct))).ConfigureAwait(false);
            var repos = folderRepos.Where(p => p.Repo is not null).GroupBy(p => p.Repo!).ToList();
            var accounts = repos.Count == 0 ? [] : await AccountsAsync(ct).ConfigureAwait(false);
            // Each result is stored as it arrives, so a slow repository does not hold back the others; the event waits for the round.
            // No answer from gh (null) makes no reads, so the last results stay; no logins at all (empty) is read as it is.
            var changes = accounts is null ? [] : await Task.WhenAll(repos.Select(g => ReadAsync(g.Key, accounts, g.First().Folder, ct))).ConfigureAwait(false);

            if (changes.Any(c => c) || folderRepos.Any(p => p.Moved))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _round.Release();
        }
    }

    /// <summary>
    /// Sets one kind's newest items from a full list (the popup read it all), and its count as <see cref="GitHubList.TotalGiven"/>
    /// has it, so the pill agrees with it before the next round. <paramref name="all"/> is newest first, as
    /// <see cref="GitHubReader.ListAsync"/> gives it. Nothing is known of a repository no round has read, so that one is left alone.
    /// </summary>
    /// <param name="since">The <see cref="Latest"/> from before the list was read (null when there was none): when another result is stored now, the list changes nothing.</param>
    /// <param name="total">The count the repository now holds for that kind, when the list was applied.</param>
    /// <returns>Whether the list was applied: not when no round has read the repository, nor when another result is stored than <paramref name="since"/>.</returns>
    public bool Replace(GitHubRepo repo, GitHubItemKind kind, GitHubList all, RepoActivity? since, out int total)
    {
        total = 0;
        lock (_gate)
        {
            if (!_results.TryGetValue(repo, out var last))
            {
                return false;
            }

            // A list read before a newer result was stored must not overwrite it. The other orders, and a round that changed only the
            // other kind, are accepted: the next refresh heals them.
            if (!ReferenceEquals(last, since))
            {
                return false;
            }

            var newest = all.Items.Take(RepoActivity.PreviewCount).ToList();
            var next = kind == GitHubItemKind.Issue
                ? last with { OpenIssues = all.TotalGiven(last.OpenIssues), NewestIssues = newest }
                : last with { OpenPullRequests = all.TotalGiven(last.OpenPullRequests), NewestPullRequests = newest };
            total = kind == GitHubItemKind.Issue ? next.OpenIssues : next.OpenPullRequests;
            if (last.SameAs(next))
            {
                return true;
            }

            _results[repo] = next;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <returns><c>Moved</c> is true when the folder now belongs to another repository than before (or to its first), which no read of the round would report when the repository was read already.</returns>
    private async Task<(string Folder, GitHubRepo? Repo, bool Moved)> RepoOfAsync(string folder, CancellationToken ct)
    {
        try
        {
            var origin = await originOf(folder, ct).ConfigureAwait(false);
            lock (_gate)
            {
                var known = _repoOfFolder.GetValueOrDefault(folder);
                // No answer (git timed out or failed) is not an origin that is gone: the folder keeps its repository.
                if (origin.Failed && known is not null)
                {
                    logger.LogWarning("Reading the origin of {Folder} failed; it keeps the repository it had", folder);
                    return (folder, known, false);
                }

                // Nothing known and no answer: no entry either, so the next git round asks again instead of waiting ten minutes.
                if (origin.Failed)
                {
                    logger.LogWarning("Reading the origin of {Folder} failed; it is asked again next round", folder);
                    return (folder, null, false);
                }

                var repo = GitHubRepo.FromRemote(origin.Url);
                _repoOfFolder[folder] = repo;
                return (folder, repo, known != repo);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Finding the GitHub repository of {Folder} failed", folder);
            lock (_gate)
            {
                return (folder, _repoOfFolder.GetValueOrDefault(folder), false);
            }
        }
    }

    /// <summary>The round's accounts. <c>gh auth status</c> checks every token against the API, so it runs once a round and not once a repository; no answer (null) means no reads, and the last results stay.</summary>
    private async Task<IReadOnlyList<string>?> AccountsAsync(CancellationToken ct)
    {
        try
        {
            return await gh.AccountsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reading the GitHub accounts failed; the last results stay");
            return null;
        }
    }

    /// <summary>Reads the repository and stores what came; true when that differs from the last result.</summary>
    private async Task<bool> ReadAsync(GitHubRepo repo, IReadOnlyList<string> accounts, string folder, CancellationToken ct)
    {
        await _reads.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var read = await reader.ReadAsync(repo, accounts, ct).ConfigureAwait(false);
            if (read.NobodySeesIt)
            {
                lock (_gate)
                {
                    if (!_results.Remove(repo))
                    {
                        return false;
                    }
                }

                logger.LogInformation("No account can see the repository of {Folder}; its result goes", folder);
                return true;
            }

            var activity = read.Activity;
            // Nothing read: the last result stays. A zero is a read like any other.
            if (activity is null)
            {
                logger.LogWarning("Reading the open issues and pull requests of {Folder} failed; the last result stays", folder);
                return false;
            }

            lock (_gate)
            {
                if (_results.TryGetValue(repo, out var last) && last.SameAs(activity))
                {
                    return false;
                }

                _results[repo] = activity;
                return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reading the open issues and pull requests of {Folder} failed; the last result stays", folder);
            return false;
        }
        finally
        {
            _reads.Release();
        }
    }

    private static string Key(string path) => Path.TrimEndingDirectorySeparator(path);
}
