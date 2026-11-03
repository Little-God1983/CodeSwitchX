using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Core.GitHub;

/// <summary>
/// The open issues and pull requests of the repositories the Yard's folders belong to, in memory only. A round reads each
/// repository once however many folders share it, four at a time; a read that fails keeps the last result, and a result
/// that did not change keeps its instance, so the tiles built on it compare equal and stay as they are.
/// </summary>
public sealed class GitHubActivity(GitHubReader reader, Func<string, CancellationToken, Task<string?>> originOf, ILogger<GitHubActivity> logger)
{
    private const int MostReadsAtOnce = 4;
    private const int PreviewCount = 5;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _round = new(1, 1);
    private readonly SemaphoreSlim _reads = new(MostReadsAtOnce, MostReadsAtOnce);
    private readonly Dictionary<string, GitHubRepo?> _repoOfFolder = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<GitHubRepo, RepoActivity> _results = [];

    /// <summary>A round changed at least one result, or <see cref="Replace"/> did; raised on whichever thread did it.</summary>
    public event EventHandler? Changed;

    /// <summary>What was last read for the repository the folder belongs to; null before the first read, or when it has no GitHub origin.</summary>
    public RepoActivity? ForFolder(string path)
    {
        lock (_gate)
        {
            return _repoOfFolder.GetValueOrDefault(Key(path)) is { } repo ? _results.GetValueOrDefault(repo) : null;
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

    /// <summary>Works out the repository of each folder, reads each repository once and raises <see cref="Changed"/> once at the end if a result differs.</summary>
    public async Task RefreshAsync(IReadOnlyCollection<string> folders, CancellationToken ct)
    {
        await _round.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var folderRepos = await Task.WhenAll(folders.Select(f => Key(f)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(folder => RepoOfAsync(folder, ct))).ConfigureAwait(false);
            var repos = folderRepos.Where(p => p.Repo is not null).GroupBy(p => p.Repo!).ToList();
            var reads = await Task.WhenAll(repos.Select(g => ReadAsync(g.Key, g.First().Folder, ct))).ConfigureAwait(false);

            var changed = false;
            lock (_gate)
            {
                foreach (var (repo, activity) in reads)
                {
                    // Nothing read: the last result stays. A zero is a read like any other.
                    if (activity is not null && (!_results.TryGetValue(repo, out var last) || !last.SameAs(activity)))
                    {
                        _results[repo] = activity;
                        changed = true;
                    }
                }
            }

            if (changed)
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
    /// Sets one kind's count and five newest from a full list (the popup read it all), so the pill agrees with it before the
    /// next round. <paramref name="all"/> is newest first, as <see cref="GitHubReader.ListAsync"/> gives it. Nothing is
    /// known of a repository no round has read, so that one is left alone.
    /// </summary>
    public void Replace(GitHubRepo repo, GitHubItemKind kind, IReadOnlyList<GitHubItem> all)
    {
        lock (_gate)
        {
            if (!_results.TryGetValue(repo, out var last))
            {
                return;
            }

            var newest = all.Take(PreviewCount).ToList();
            var next = kind == GitHubItemKind.Issue
                ? last with { OpenIssues = all.Count, NewestIssues = newest }
                : last with { OpenPullRequests = all.Count, NewestPullRequests = newest };
            if (last.SameAs(next))
            {
                return;
            }

            _results[repo] = next;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<(string Folder, GitHubRepo? Repo)> RepoOfAsync(string folder, CancellationToken ct)
    {
        try
        {
            var repo = GitHubRepo.FromRemote(await originOf(folder, ct).ConfigureAwait(false));
            lock (_gate)
            {
                _repoOfFolder[folder] = repo;
            }

            return (folder, repo);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Finding the GitHub repository of {Folder} failed", folder);
            lock (_gate)
            {
                return (folder, _repoOfFolder.GetValueOrDefault(folder));
            }
        }
    }

    private async Task<(GitHubRepo Repo, RepoActivity? Activity)> ReadAsync(GitHubRepo repo, string folder, CancellationToken ct)
    {
        await _reads.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var activity = await reader.ReadAsync(repo, ct).ConfigureAwait(false);
            if (activity is null && !ct.IsCancellationRequested)
            {
                logger.LogWarning("Reading the open issues and pull requests of {Folder} failed; the last result stays", folder);
            }

            return (repo, activity);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reading the open issues and pull requests of {Folder} failed; the last result stays", folder);
            return (repo, null);
        }
        finally
        {
            _reads.Release();
        }
    }

    private static string Key(string path) => Path.TrimEndingDirectorySeparator(path);
}
