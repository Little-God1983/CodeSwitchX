using CodeSwitchX.Core.GitHub;
using CodeSwitchX.Core.Paths;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Infrastructure;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Yard;

/// <summary>How a job on a checkout ended: the line the panel shows.</summary>
/// <param name="Checkout">The checkout's folder, as the job was given it.</param>
/// <param name="Name">The repository's name, for a line that is not in its panel.</param>
/// <param name="Merged">The pull request a merge job merged (#408), whatever the pull after it came to; null when nothing was merged.</param>
/// <param name="Pulled">The checkout is on the default branch and up to date: a pull, or the pull after a merge, went through.</param>
public sealed record GitJobOutcome(string Checkout, string Name, bool Succeeded, string Message, int? Merged = null, bool Pulled = false);

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

    /// <summary>The same folder, written either way, is the same checkout.</summary>
    public static string Key(string checkout) => PathNormalizer.Normalize(checkout);

    public bool IsRunning(string checkout) => _running.ContainsKey(Key(checkout));

    /// <summary>What the checkout's running job does ("Pulling main…", "Merging #405…"), or null when none runs.</summary>
    public string? RunningText(string checkout) => _running.GetValueOrDefault(Key(checkout));

    /// <summary>The outcome of the checkout's last job, once: a panel shows it, and no other panel shows it again.</summary>
    public GitJobOutcome? TakeOutcome(string checkout) =>
        _outcomes.Remove(Key(checkout), out var left) && _time.GetUtcNow() - left.At < OutcomeLifetime ? left.Outcome : null;

    /// <summary>
    /// Switches the checkout to <paramref name="branch"/> and pulls it (<see cref="GitPuller"/>), unless a job of it runs already. The
    /// task ends when the outcome has been handed out, for tests; nobody needs to wait for it.
    /// </summary>
    /// <param name="chatIsWorking">Asked on the UI thread right before git changes anything, after the fetch: a chat that started working meanwhile stops the pull.</param>
    public Task PullAsync(string checkout, string branch, string name, Func<bool> chatIsWorking) =>
        Run(checkout, name, $"Pulling {branch}…", "A pull", async () =>
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
    public Task MergeAsync(string checkout, string name, GitHubRepo repo, string account, int number, string baseBranch, string? pullBranch, Func<bool> chatIsWorking)
    {
        if (_merger is null)
        {
            return Task.CompletedTask;
        }

        return Run(checkout, name, $"Merging #{number}…", "A merge", async () =>
        {
            var merge = await _merger.MergeAsync(repo, account, number, baseBranch, CancellationToken.None).ConfigureAwait(false);
            if (!merge.Merged)
            {
                return new GitJobOutcome(checkout, name, false, $"Can't merge #{number}: {merge.Text}");
            }

            if (pullBranch is null)
            {
                return new GitJobOutcome(checkout, name, true, $"Merged #{number}.", number);
            }

            var pull = await _puller.PullAsync(checkout, pullBranch, name, CancellationToken.None, _ => StillFreeAsync(chatIsWorking)).ConfigureAwait(false);
            return pull.Succeeded
                ? new GitJobOutcome(checkout, name, true, $"Merged #{number}. {pull.Text}", number, Pulled: true)
                : new GitJobOutcome(checkout, name, false, $"Merged #{number}, but didn't switch: {pull.Text}", number);
        }, $"#{number} may or may not be merged: something went wrong. See the log and GitHub.");
    }

    /// <param name="what">"A pull", "A merge": for the log, which names no repository.</param>
    /// <param name="failed">What the panel says when the job threw.</param>
    private Task Run(string checkout, string name, string running, string what, Func<Task<GitJobOutcome>> work, string failed)
    {
        var key = Key(checkout);
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
                outcome = await work().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The type only: a message could carry a path, a branch or a repository's name.
                _logger.LogWarning("{Job} from the Yard failed: {ExceptionType}", what, ex.GetType().Name);
                outcome = new GitJobOutcome(checkout, name, false, failed);
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
