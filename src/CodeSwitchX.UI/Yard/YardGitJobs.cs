using CodeSwitchX.Core.GitHub;
using CodeSwitchX.Core.Paths;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Infrastructure;
using Microsoft.Extensions.Logging;
using Path = System.IO.Path;

namespace CodeSwitchX.UI.Yard;

/// <summary>How a job on a checkout ended: the line the panel shows.</summary>
/// <param name="Checkout">The checkout's folder, as the job was given it.</param>
/// <param name="Name">The repository's name, for a line that is not in its panel.</param>
/// <param name="Merged">The pull request a merge job merged (#408), whatever the pull after it came to; null when nothing was merged.</param>
/// <param name="Pulled">The checkout is on the default branch and up to date: a pull, or the pull after a merge, went through.</param>
/// <param name="ChangedGitHub">A merge job that merged, or may have (gh ran out of time, the job threw): the Yard reads GitHub again. A refusal changed nothing.</param>
/// <param name="Key">The checkout's key the job ran under (<see cref="YardGitJobs.Key"/>).</param>
/// <param name="Reread">The pull request moved since the panel read it (#414): the panel reads its list again before another try.</param>
/// <param name="Tree">The working tree the job ran in, by its own git directory: a subfolder's line shares it, a linked worktree has one of its own.</param>
public sealed record GitJobOutcome(string Checkout, string Name, bool Succeeded, string Message, int? Merged = null, bool Pulled = false, bool ChangedGitHub = false,
    string Key = "", string Tree = "", bool Reread = false);

/// <summary>
/// The pulls (#407) and merges (#408) the GitHub panel starts, kept at the Yard's level so they outlive the panel. A job once started is
/// never cancelled: killing git or gh midway leaves the result unknown or a lock behind. One job at a time per checkout. Everything here
/// runs on the UI thread but the git and gh themselves; the outcome waits for a panel of that checkout to take it, for
/// <see cref="OutcomeLifetime"/>, and one nobody takes is the Yard's too (<see cref="Ended"/>).
/// </summary>
public sealed class YardGitJobs
{
    private readonly GitPuller _puller;
    private readonly GitHubMerger? _merger;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    // By checkout key: what the running job does ("Pulling main…"), and the outcome no panel has taken yet, with when it ended. UI thread only.
    private readonly Dictionary<string, string> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (GitJobOutcome Outcome, DateTimeOffset At)> _outcomes = new(StringComparer.Ordinal);

    /// <summary>How long an outcome waits for a panel: a panel opened the next day must not say "3 new commits" of a pull long gone (review of #407).</summary>
    internal static readonly TimeSpan OutcomeLifetime = TimeSpan.FromMinutes(15);

    /// <summary>How long the check before the switch waits for the UI thread to read the chats; a busy one stops the pull.</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    /// <param name="merger">Merges on GitHub; none (tests, no gh) means the panel offers no merge.</param>
    public YardGitJobs(GitPuller puller, IUiDispatcher ui, TimeProvider time, ILogger logger, GitHubMerger? merger = null)
    {
        _puller = puller;
        _merger = merger;
        _ui = ui;
        _time = time;
        _logger = logger;
    }

    /// <summary>A job of the checkout (its key, <see cref="Key"/>) started or ended; a panel of it looks again. On the UI thread.</summary>
    public event Action<string>? Changed;

    /// <summary>A job ended, after <see cref="Changed"/>; <c>taken</c> says whether a panel took the outcome. On the UI thread.</summary>
    public event Action<GitJobOutcome, bool>? Ended;

    /// <summary>The panel can offer a merge.</summary>
    public bool CanMerge => _merger is not null;

    /// <summary>
    /// What a checkout is known by: its git directory, normalized, so lines on two folders of one checkout share one job (#412); its folder
    /// where git could not tell. The same path written either way is the same key. The methods below take a key or a folder.
    /// </summary>
    public static string Key(string gitDirOrFolder) => PathNormalizer.Normalize(gitDirOrFolder);

    /// <summary>
    /// The git directory a linked worktree shares with its main checkout (<c>repo\.git</c> for <c>repo\.git\worktrees\name</c>): the refs a
    /// pull fetches and moves live there (review of #412). Any other git directory is its own; null stays null.
    /// </summary>
    public static string? CommonGitDir(string? gitDir)
    {
        if (gitDir is null)
        {
            return null;
        }

        // Only where git puts them: <repo>\.git\worktrees\<name>, or <repo>.git\worktrees\<name> of a bare one. A repository whose own folder
        // is called "worktrees" (E:\Repos\worktrees\.git) is no linked worktree (review of #412).
        var trimmed = Path.TrimEndingDirectorySeparator(gitDir);
        var parent = Path.GetDirectoryName(trimmed);
        var common = parent is null ? null : Path.GetDirectoryName(parent);
        return parent is not null && common is not null
            && string.Equals(Path.GetFileName(parent), "worktrees", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(common).EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? common
            : trimmed;
    }

    public bool IsRunning(string checkout) => _running.ContainsKey(Key(checkout));

    /// <summary>What the checkout's running job does ("Pulling main…", "Merging #405…"), or null when none runs.</summary>
    public string? RunningText(string checkout) => _running.GetValueOrDefault(Key(checkout));

    /// <summary>The outcome of the checkout's last job, once: a panel shows it, and no other panel shows it again.</summary>
    /// <param name="tree">
    /// The working tree of the panel that asks (<see cref="PanelCheckout.Tree"/>): an outcome is only for a panel of the working tree the job ran
    /// in, as a linked worktree and its main checkout share one key, while a subfolder's line shares the tree (review of #412). One for another
    /// tree stays, for its own panel or the Yard. None asks for any.
    /// </param>
    public GitJobOutcome? TakeOutcome(string checkout, string? tree = null)
    {
        var key = Key(checkout);
        if (!_outcomes.TryGetValue(key, out var left) || (tree is not null && left.Outcome.Tree != Key(tree)))
        {
            return null;
        }

        _outcomes.Remove(key);
        return _time.GetUtcNow() - left.At < OutcomeLifetime ? left.Outcome : null;
    }

    /// <summary>
    /// Switches the checkout to <paramref name="branch"/> and pulls it (<see cref="GitPuller"/>), unless a job of it runs already. The
    /// task ends when the outcome has been handed out, for tests; nobody needs to wait for it.
    /// </summary>
    /// <param name="chatIsWorking">Asked on the UI thread right before git changes anything, after the fetch: a chat that started working meanwhile stops the pull.</param>
    /// <param name="key">The checkout's key (<see cref="PanelCheckout.Key"/>); its folder's when none is given.</param>
    /// <param name="tree">The working tree's own git directory (<see cref="PanelCheckout.Tree"/>); the folder's when none is given.</param>
    public Task PullAsync(string checkout, string branch, string name, Func<bool> chatIsWorking, string? key = null, string? tree = null) =>
        Run(checkout, key, tree, name, $"Pulling {branch}…", "A pull", async () =>
        {
            var pull = await _puller.PullAsync(checkout, branch, name, CancellationToken.None, _ => StillFreeAsync(chatIsWorking)).ConfigureAwait(false);
            return new GitJobOutcome(checkout, name, pull.Succeeded, pull.Succeeded ? pull.Text : $"Pull stopped: {pull.Text}", Pulled: pull.Succeeded);
        }, "Pull stopped: something went wrong. See the log.");

    /// <summary>
    /// Merges pull request <paramref name="number"/> on GitHub (#408) and, given <paramref name="pullBranch"/>, then switches the checkout to it
    /// and pulls, as <see cref="PullAsync"/>. Unless a job of the checkout runs already.
    /// </summary>
    /// <param name="baseBranch">What the pull request merges into, for the conflict's reason.</param>
    /// <param name="pullBranch">The default branch for Merge and pull; null for Merge only.</param>
    public Task MergeAsync(string checkout, string name, GitHubRepo repo, string account, int number, string baseBranch, string? pullBranch, Func<bool> chatIsWorking,
        string? key = null, string? tree = null, string? headSha = null)
    {
        if (_merger is null)
        {
            return Task.CompletedTask;
        }

        return Run(checkout, key, tree, name, $"Merging #{number}…", "A merge", async () =>
        {
            var merge = await _merger.MergeAsync(repo, account, number, baseBranch, CancellationToken.None, headSha).ConfigureAwait(false);
            if (merge.Queued)
            {
                // Not merged yet: nothing is pulled and the row stays; the pills are read again.
                return new GitJobOutcome(checkout, name, true, $"#{number} will be added to the merge queue. Pull when GitHub has merged it.", ChangedGitHub: true);
            }

            if (!merge.Merged)
            {
                return new GitJobOutcome(checkout, name, false, $"Can't merge #{number}: {merge.Text}", ChangedGitHub: merge.Unknown || merge.Moved, Reread: merge.Moved);
            }

            var merged = merge.AlreadyMerged ? $"#{number} was merged already" : $"Merged #{number}";
            if (pullBranch is null)
            {
                return new GitJobOutcome(checkout, name, true, $"{merged}.", number, ChangedGitHub: true);
            }

            var pull = await _puller.PullAsync(checkout, pullBranch, name, CancellationToken.None, _ => StillFreeAsync(chatIsWorking), leavePushedOnly: true)
                .ConfigureAwait(false);
            return pull.Succeeded
                ? new GitJobOutcome(checkout, name, true, $"{merged}. {pull.Text}", number, Pulled: true, ChangedGitHub: true)
                : new GitJobOutcome(checkout, name, false, $"{merged}, but didn't switch: {pull.Text}", number, ChangedGitHub: true);
        }, $"#{number} may or may not be merged: something went wrong. See the log and GitHub.", fromMerge: true);
    }

    /// <param name="what">"A pull", "A merge": for the log, which names no repository.</param>
    /// <param name="failed">What the panel says when the job threw.</param>
    /// <param name="fromMerge">A merge job: one that threw may have merged.</param>
    private Task Run(string checkout, string? checkoutKey, string? checkoutTree, string name, string running, string what, Func<Task<GitJobOutcome>> work, string failed, bool fromMerge = false)
    {
        var key = Key(checkoutKey ?? checkout);
        var tree = Key(checkoutTree ?? checkout);
        if (!_running.TryAdd(key, running))
        {
            return Task.CompletedTask;
        }

        _outcomes.Remove(key);
        Changed?.Invoke(key);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Off the UI thread: git and gh start before their first await.
        _ = Task.Run(async () =>
        {
            GitJobOutcome outcome;
            try
            {
                outcome = await work().ConfigureAwait(false) with { Key = key, Tree = tree };
            }
            catch (Exception ex)
            {
                // The type only: a message could carry a path, a branch or a repository's name.
                _logger.LogWarning("{Job} from the Yard failed: {ExceptionType}", what, ex.GetType().Name);
                outcome = new GitJobOutcome(checkout, name, false, failed, ChangedGitHub: fromMerge, Key: key, Tree: tree);
            }

            _logger.LogInformation("{Job} from the Yard ended: {Succeeded}", what, outcome.Succeeded);
            _ui.Post(() =>
            {
                try
                {
                    End(key, outcome);
                }
                finally
                {
                    ended.TrySetResult();
                }
            });
        });
        return ended.Task;
    }

    /// <summary>Null when no chat works in the checkout now; the reason to stop otherwise, and when the UI thread does not answer in time.</summary>
    private async Task<string?> StillFreeAsync(Func<bool> chatIsWorking)
    {
        try
        {
            return await _ui.InvokeAsync(chatIsWorking, CheckTimeout).ConfigureAwait(false) ? "a chat started working here." : null;
        }
        catch (TimeoutException)
        {
            return "the Yard didn't answer whether a chat is working here.";
        }
    }

    private void End(string key, GitJobOutcome outcome)
    {
        _running.Remove(key);
        _outcomes[key] = (outcome, _time.GetUtcNow());
        Changed?.Invoke(key);
        Ended?.Invoke(outcome, !_outcomes.ContainsKey(key));
    }
}
