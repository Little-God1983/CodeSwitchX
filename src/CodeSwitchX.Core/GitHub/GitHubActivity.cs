using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Core.GitHub;

/// <summary>
/// The open issues and pull requests of the repositories the Yard's folders belong to, in memory only. A round reads the
/// accounts once and each repository once however many folders share it, four at a time; a read that fails keeps the last
/// result, one every account answers with "not found" drops it, and a result that did not change keeps its instance, so the
/// tiles built on it compare equal and stay as they are. A round raises <see cref="Changed"/> when a result changed or a folder
/// now belongs to another repository. <see cref="Retain"/> lets go of what no folder on the tiles needs any more.
/// A round logs what failed once per kind of failure (a folder whose origin could not be told, a repository that could not be read), not once
/// per folder or repository: the Warnings carry only counts and kinds, and a folder path appears only in a Debug line.
/// </summary>
/// <param name="hostOfAlias">What <c>ssh -G</c> makes of an ssh host name in a remote such as <c>git@github-work:owner/repo</c> (null: no alias is looked up).</param>
public sealed class GitHubActivity(IGhCli gh, GitHubReader reader, Func<string, CancellationToken, Task<OriginLookup>> originOf, ILogger<GitHubActivity> logger, Func<string, CancellationToken, Task<string?>>? hostOfAlias = null)
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
            foreach (var failure in folderRepos.Select(p => p.Failure).OfType<Fail>().GroupBy(f => f.Kind))
            {
                logger.LogWarning("GitHub round: {Failed} of {Total} folders could not be resolved to a repository: {Failure}", failure.Count(), folderRepos.Length, failure.First().Detail);
            }

            var repos = folderRepos.Where(p => p.Repo is not null).GroupBy(p => p.Repo!).ToList();
            var accounts = repos.Count == 0 ? [] : await AccountsAsync(ct).ConfigureAwait(false);
            // Each result is stored as it arrives, so a slow repository does not hold back the others; the event waits for the round.
            // No answer from gh (null) makes no reads, so the last results stay; no logins at all (empty) is read as it is.
            var reads = accounts is null ? [] : await Task.WhenAll(repos.Select(g => ReadAsync(g.Key, accounts, g.First().Folder, ct))).ConfigureAwait(false);
            foreach (var failure in reads.Select(r => r.Failure).OfType<Fail>().GroupBy(f => f.Kind))
            {
                logger.LogWarning("GitHub round: {Failed} of {Total} repositories could not be read: {Failure}", failure.Count(), reads.Length, failure.First().Detail);
            }

            if (reads.Any(r => r.Changed) || folderRepos.Any(p => p.Moved))
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

    /// <returns>
    /// <c>Moved</c> is true when the folder now belongs to another repository than before (or to its first), which no read of the round would report when the repository was read already.
    /// <c>Failure</c> names why git could not tell the folder's origin this time, for the round to count; the folder then keeps the repository it had, and has no entry if it had none,
    /// so the next round asks again instead of waiting ten minutes. An ssh host that has no answer is no failure: the folder is recorded with no repository, and keeps the one it had.
    /// </returns>
    private async Task<(string Folder, GitHubRepo? Repo, bool Moved, Fail? Failure)> RepoOfAsync(string folder, CancellationToken ct)
    {
        try
        {
            var origin = await originOf(folder, ct).ConfigureAwait(false);
            var (parsed, noAnswer) = origin.Failed ? (null, false) : await RepoOfRemoteAsync(origin.Url, ct).ConfigureAwait(false);
            lock (_gate)
            {
                var known = _repoOfFolder.GetValueOrDefault(folder);
                // No answer (git timed out or failed) is not an origin that is gone.
                if (origin.Failed)
                {
                    logger.LogDebug("Reading the origin of {Folder} failed; it keeps the repository it had, if any", folder);
                    return (folder, known, false, new Fail("git gave no answer"));
                }

                // Neither is ssh with nothing to say about the host: it never clears a repository, and with none it is recorded, so it is not asked every pass.
                if (noAnswer && known is not null)
                {
                    return (folder, known, false, null);
                }

                _repoOfFolder[folder] = parsed;
                return (folder, parsed, known != parsed, null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failure = new Fail(ex.GetType().Name, FailureText.Of(ex));
            logger.LogDebug("Finding the GitHub repository of {Folder} failed: {Failure}", folder, failure.Detail);
            lock (_gate)
            {
                return (folder, _repoOfFolder.GetValueOrDefault(folder), false, failure);
            }
        }
    }

    /// <summary>
    /// The repository a remote address points at. An ssh host that is not GitHub by name is asked about (<c>hostOfAlias</c>); <c>NoAnswer</c> is true
    /// when that gave no host and the remote is not GitHub by the <c>github.com-name</c> convention either.
    /// </summary>
    private async Task<(GitHubRepo? Repo, bool NoAnswer)> RepoOfRemoteAsync(string? url, CancellationToken ct)
    {
        if (GitHubRepo.Parse(url) is not { } address)
        {
            return (null, false);
        }

        string? host = null;
        if (address.NeedsLookup && hostOfAlias is not null)
        {
            try
            {
                host = await hostOfAlias(address.Host, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug("Looking up an ssh host name failed: {Failure}", FailureText.Of(ex));
            }
        }

        var repo = address.RepoGiven(host);
        return (repo, address.NeedsLookup && host is null && repo is null);
    }

    /// <summary>The round accounts. <c>gh auth status</c> checks every token against the API, so it runs once a round and not once a repository; no answer (null) means no reads, and the last results stay.</summary>
    private async Task<IReadOnlyList<string>?> AccountsAsync(CancellationToken ct)
    {
        try
        {
            var accounts = await gh.AccountsAsync(ct).ConfigureAwait(false);
            if (accounts.Logins is null)
            {
                logger.LogWarning("GitHub round: the accounts could not be read ({Failure}); the last results stay", accounts.Failure);
            }

            return accounts.Logins;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("GitHub round: the accounts could not be read ({Failure}); the last results stay", FailureText.Of(ex));
            return null;
        }
    }

    /// <summary>Reads the repository and stores what came. <c>Changed</c> is true when that differs from the last result; <c>Failure</c> names the kind of failure when nothing was read (the last result stays), for the round to count.</summary>
    private async Task<(bool Changed, Fail? Failure)> ReadAsync(GitHubRepo repo, IReadOnlyList<string> accounts, string folder, CancellationToken ct)
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
                        return (false, null);
                    }
                }

                logger.LogInformation("No account can see the repository of {Folder}; its result goes", folder);
                return (true, null);
            }

            var activity = read.Activity;
            // Nothing read: the last result stays. A zero is a read like any other.
            if (activity is null)
            {
                logger.LogDebug("Reading the open issues and pull requests of {Folder} failed; the last result stays", folder);
                return (false, new Fail("no answer"));
            }

            lock (_gate)
            {
                if (_results.TryGetValue(repo, out var last) && last.SameAs(activity))
                {
                    return (false, null);
                }

                _results[repo] = activity;
                return (true, null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failure = new Fail(ex.GetType().Name, FailureText.Of(ex));
            logger.LogDebug("Reading the open issues and pull requests of {Folder} failed ({Failure}); the last result stays", folder, failure.Detail);
            return (false, failure);
        }
        finally
        {
            _reads.Release();
        }
    }

    /// <summary>A kind of failure, for the round to count; <c>Detail</c> is what a Warning shows of the first: the exception type and its stack, never its message.</summary>
    private sealed record Fail(string Kind, string? Detail = null)
    {
        public string Detail { get; } = Detail ?? Kind;
    }

    private static string Key(string path) => Path.TrimEndingDirectorySeparator(path);
}
