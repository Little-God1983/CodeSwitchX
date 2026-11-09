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
/// but the git itself; the outcome waits for a panel of that checkout to take it, and one nobody takes is the Yard's (<see cref="Ended"/>).
/// </summary>
public sealed class YardGitJobs
{
    private readonly GitPuller _puller;
    private readonly IUiDispatcher _ui;
    private readonly ILogger _logger;
    // By checkout key: what the running job is doing ("Pulling main..."), and the outcome no panel has taken yet. UI thread only.
    private readonly Dictionary<string, string> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GitJobOutcome> _outcomes = new(StringComparer.Ordinal);

    public YardGitJobs(GitPuller puller, IUiDispatcher ui, ILogger logger)
    {
        _puller = puller;
        _ui = ui;
        _logger = logger;
    }

    /// <summary>A job of the checkout (its key, <see cref="Key"/>) started or ended; a panel of it looks again. On the UI thread.</summary>
    public event Action<string>? Changed;

    /// <summary>A job ended, after <see cref="Changed"/>; <c>taken</c> says whether a panel took the outcome. On the UI thread.</summary>
    public event Action<GitJobOutcome, bool>? Ended;

    /// <summary>The same folder, written either way, is the same checkout.</summary>
    public static string Key(string checkout) => PathNormalizer.Normalize(checkout);

    public bool IsRunning(string checkout) => _running.ContainsKey(Key(checkout));

    /// <summary>What the checkout's running job is doing, or null when none runs.</summary>
    public string? RunningText(string checkout) => _running.GetValueOrDefault(Key(checkout));

    /// <summary>The outcome of the checkout's last job, once: a panel shows it, and no other panel shows it again.</summary>
    public GitJobOutcome? TakeOutcome(string checkout) => _outcomes.Remove(Key(checkout), out var outcome) ? outcome : null;

    /// <summary>
    /// Switches the checkout to <paramref name="branch"/> and pulls it (<see cref="GitPuller"/>), unless a job of it runs already. The
    /// task ends when the outcome has been handed out, for tests; nobody needs to wait for it.
    /// </summary>
    public Task PullAsync(string checkout, string branch, string name)
    {
        var key = Key(checkout);
        if (!_running.TryAdd(key, $"Pulling {branch}..."))
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
                var pull = await _puller.PullAsync(checkout, branch, name, CancellationToken.None).ConfigureAwait(false);
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

    private void End(string key, GitJobOutcome outcome)
    {
        _running.Remove(key);
        _outcomes[key] = outcome;
        Changed?.Invoke(key);
        Ended?.Invoke(outcome, !_outcomes.ContainsKey(key));
    }
}
