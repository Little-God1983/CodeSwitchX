using CodeSwitchX.Core.Paths;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Infrastructure;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Yard;

/// <summary>How a job on a checkout ended: the line the panel shows.</summary>
/// <param name="Checkout">The checkout's folder, as the job was given it.</param>
public sealed record GitJobOutcome(string Checkout, bool Succeeded, string Message);

/// <summary>
/// The pulls the GitHub panel starts (#407), kept at the Yard's level so they outlive the panel. A job once started is never cancelled:
/// killing git midway leaves the result unknown or a lock behind. One job at a time per checkout. Everything here runs on the UI thread
/// but the git itself; the outcome waits for a panel of that checkout to take it, for <see cref="OutcomeLifetime"/>, and one nobody takes
/// is the Yard's too (<see cref="Ended"/>).
/// </summary>
public sealed class YardGitJobs
{
    private readonly GitPuller _puller;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    // By checkout key: the checkouts a job runs on, and the outcome no panel has taken yet, with when it ended. UI thread only.
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (GitJobOutcome Outcome, DateTimeOffset At)> _outcomes = new(StringComparer.Ordinal);

    /// <summary>How long an outcome waits for a panel: a panel opened the next day must not say "3 new commits" of a pull long gone (review of #407).</summary>
    internal static readonly TimeSpan OutcomeLifetime = TimeSpan.FromMinutes(15);

    /// <summary>How long the check before the switch waits for the UI thread to read the chats; a busy one stops the pull.</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    public YardGitJobs(GitPuller puller, IUiDispatcher ui, TimeProvider time, ILogger logger)
    {
        _puller = puller;
        _ui = ui;
        _time = time;
        _logger = logger;
    }

    /// <summary>A job of the checkout (its key, <see cref="Key"/>) started or ended; a panel of it looks again. On the UI thread.</summary>
    public event Action<string>? Changed;

    /// <summary>A job ended, after <see cref="Changed"/>; <c>taken</c> says whether a panel took the outcome. On the UI thread.</summary>
    public event Action<GitJobOutcome, bool>? Ended;

    /// <summary>The same folder, written either way, is the same checkout.</summary>
    public static string Key(string checkout) => PathNormalizer.Normalize(checkout);

    public bool IsRunning(string checkout) => _running.Contains(Key(checkout));

    /// <summary>The outcome of the checkout's last job, once: a panel shows it, and no other panel shows it again.</summary>
    public GitJobOutcome? TakeOutcome(string checkout) =>
        _outcomes.Remove(Key(checkout), out var left) && _time.GetUtcNow() - left.At < OutcomeLifetime ? left.Outcome : null;

    /// <summary>
    /// Switches the checkout to <paramref name="branch"/> and pulls it (<see cref="GitPuller"/>), unless a job of it runs already. The
    /// task ends when the outcome has been handed out, for tests; nobody needs to wait for it.
    /// </summary>
    /// <param name="chatIsWorking">Asked on the UI thread right before git changes anything, after the fetch: a chat that started working meanwhile stops the pull.</param>
    public Task PullAsync(string checkout, string branch, string name, Func<bool> chatIsWorking)
    {
        var key = Key(checkout);
        if (!_running.Add(key))
        {
            return Task.CompletedTask;
        }

        _outcomes.Remove(key);
        Changed?.Invoke(key);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Off the UI thread: git starts before the first await.
        _ = Task.Run(async () =>
        {
            GitJobOutcome outcome;
            try
            {
                // Never cancelled, see the class.
                var pull = await _puller.PullAsync(checkout, branch, name, CancellationToken.None, _ => StillFreeAsync(chatIsWorking)).ConfigureAwait(false);
                outcome = new GitJobOutcome(checkout, pull.Succeeded, pull.Succeeded ? pull.Text : $"Pull stopped: {pull.Text}");
            }
            catch (Exception ex)
            {
                // The type only: a message could carry a path or a branch name.
                _logger.LogWarning("A pull from the Yard failed: {ExceptionType}", ex.GetType().Name);
                outcome = new GitJobOutcome(checkout, false, "Pull stopped: something went wrong. See the log.");
            }

            _logger.LogInformation("A pull from the Yard ended: {Succeeded}", outcome.Succeeded);
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
