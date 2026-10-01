using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Yard;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Conductor;

/// <summary>
/// Raven's brain on Claude Code: one long-lived <c>claude -p</c> that takes the turns as stream-json on its standard input,
/// so the conversation carries on from turn to turn. It works only through the Yard: no built-in tools at all, the Yard's MCP tools
/// from <c>mcp.json</c> and no others, anything not allowed denied without asking, and a working folder that is no
/// repository. The user's settings are loaded (a proxy, a base URL or an API key helper in them is how some users reach
/// the API at all), but with every hook turned off, so their hooks (the Yard's own, RAIVEN's) do not fire for its turns;
/// nothing of it is saved as a session. A process that dies is started again for the next turn, which says so; one whose
/// model no longer is the one set is replaced, and so is one that could not connect to the Yard (a few times) and one
/// left quiet for <see cref="QuietReset"/>.
/// </summary>
public sealed class ClaudeCliBrain : IConductorBrain, IDisposable
{
    /// <summary>How long a turn waits for the next line before it gives the process up: a tool call into the app takes milliseconds.</summary>
    public static readonly TimeSpan Silence = TimeSpan.FromSeconds(90);

    /// <summary>
    /// After this long without a question the next one starts a new conversation: the Yard has moved on since the answers
    /// in it, and a conversation kept all day sends more tokens with every turn.
    /// </summary>
    public static readonly TimeSpan QuietReset = TimeSpan.FromMinutes(20);

    /// <summary>How long an interrupted turn has to end; Claude Code ends one within a second (CLI 2.1.285).</summary>
    public static readonly TimeSpan InterruptTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How often a process whose Yard tools failed to connect is replaced in a row; Claude Code does not connect again by itself.</summary>
    internal const int MaxYardRetries = 2;

    /// <summary>Hooks off, everything else of the user's settings kept.</summary>
    internal const string NoHooks = """{"disableAllHooks":true}""";

    /// <summary>Every tool of the Yard's server, and only those.</summary>
    internal const string AllowedTools = "mcp__" + YardMcp.ServerName;

    private readonly AppPaths _paths;
    private readonly BrainSettings _settings;
    private readonly IBrainProcessLauncher _launcher;
    private readonly Func<string?> _findClaude;
    private readonly TimeProvider _time;
    private readonly ILogger<ClaudeCliBrain> _logger;
    private readonly SemaphoreSlim _turns = new(1, 1);

    /// <summary>What the next turn says before its answer: a restart, a switched model. Touched while holding <see cref="_turns"/>.</summary>
    private readonly List<BrainEvent> _notices = [];
    private IBrainProcess? _process;
    private string? _processModel;

    /// <summary>The Yard's tools were reported as not connected, and have not been seen connected since; kept across a restart.</summary>
    private bool _yardWarned;

    /// <summary>Processes replaced in a row because their Yard tools failed; back to 0 once they connect.</summary>
    private int _yardRetries;

    /// <summary>This turn's process is to be replaced once the turn is over: its Yard tools failed.</summary>
    private bool _replaceAfterTurn;

    /// <summary>When the last turn ended, or the process started.</summary>
    private DateTimeOffset _lastTurnAt;

    /// <summary>Set once the app disposes the brain: nothing starts a process after that.</summary>
    private volatile bool _disposed;

    /// <summary>Interrupts sent so far, to number their requests.</summary>
    private long _interrupts;

    /// <summary>Why the last process went, when it went on its own: the next start says so.</summary>
    private string? _lost;

    public ClaudeCliBrain(AppPaths paths, BrainSettings settings, IBrainProcessLauncher launcher, Func<string?> findClaude, TimeProvider time,
        ILogger<ClaudeCliBrain> logger)
    {
        _paths = paths;
        _settings = settings;
        _launcher = launcher;
        _findClaude = findClaude;
        _time = time;
        _logger = logger;
    }

    public async IAsyncEnumerable<BrainEvent> AskAsync(string text, [EnumeratorCancellation] CancellationToken ct)
    {
        // Off the caller's thread first, the panel's UI thread: looking for claude.exe, starting it and killing an old
        // process tree would otherwise run there whenever no turn is queued ahead.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        await _turns.WaitAsync(ct).ConfigureAwait(false);
        IBrainProcess? process = null;
        var finished = false;
        try
        {
            var failure = EnsureRunning();
            foreach (var notice in _notices)
            {
                yield return notice;
            }

            _notices.Clear();
            if (failure is not null || _process is null)
            {
                finished = true;
                yield return new BrainFailed(failure ?? "Raven's brain is not running.");
                yield break;
            }

            process = _process;
            if (!await SendAsync(process, text, ct).ConfigureAwait(false))
            {
                finished = true;
                yield return new BrainFailed(await LoseAsync(process, "stopped before it could take the question").ConfigureAwait(false));
                yield break;
            }

            while (true)
            {
                var (line, timedOut) = await NextLineAsync(process, ct).ConfigureAwait(false);
                if (timedOut)
                {
                    finished = true;
                    var why = $"gave no answer for {Silence.TotalSeconds:0} s";
                    Lose(process, why);
                    yield return new BrainFailed($"Raven's brain {why}. Ask again.");
                    yield break;
                }

                if (line is null)
                {
                    finished = true;
                    yield return new BrainFailed(await LoseAsync(process, "stopped in the middle of an answer").ConfigureAwait(false));
                    yield break;
                }

                switch (ClaudeStream.Read(line))
                {
                    case ClaudeInit init:
                        // Every turn's init says how the tools stand; a failure is told once, and again only after they
                        // were seen working in between. "pending" is no failure: the server is still being connected to.
                        // A failure replaces the process after the turn, a few times in a row, since Claude Code does not
                        // connect again by itself.
                        if (YardProblem(init) is not { } problem)
                        {
                            _yardWarned = false;
                            _yardRetries = 0;
                        }
                        else if (problem.Length > 0)
                        {
                            _replaceAfterTurn = _yardRetries < MaxYardRetries;
                            if (!_yardWarned)
                            {
                                _yardWarned = true;
                                yield return new BrainNotice(problem + (_replaceAfterTurn ? " Raven tries again with the next question." : ""), Warning: true);
                            }
                        }

                        break;
                    case ClaudeEvents { Events: var events }:
                        foreach (var e in events)
                        {
                            yield return e;
                        }

                        break;
                    case ClaudeTurnOver over:
                        finished = true;
                        if (over.Error is { } error)
                        {
                            _logger.LogWarning("Raven's brain could not answer: {Error}", error);
                            yield return new BrainFailed($"Raven's brain could not answer: {error}");
                        }

                        yield break;
                }
            }
        }
        finally
        {
            // Cancelled, or left before the turn was over: it is interrupted and read to its end, so the brain keeps the
            // conversation and the rest of its lines are not read as the next turn's. One that does not end is stopped.
            if (!finished && process is not null && ReferenceEquals(process, _process)
                && !await InterruptAsync(process).ConfigureAwait(false))
            {
                Stop();
            }

            if (_replaceAfterTurn)
            {
                _replaceAfterTurn = false;
                _yardRetries++;
                Stop();
            }

            _lastTurnAt = _time.GetUtcNow();
            _turns.Release();
        }
    }

    public void WarmUp() => _ = Task.Run(async () =>
    {
        try
        {
            if (_disposed)
            {
                return;
            }

            await _turns.WaitAsync().ConfigureAwait(false);
            try
            {
                // A failure is the turn's to report, when it comes; a restart's notice waits for it too.
                EnsureRunning();
            }
            finally
            {
                _turns.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Warming up Raven's brain failed");
        }
    });

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The app's host disposes its services synchronously as it exits, and a service that only disposes asynchronously
    /// fails that. Does not wait for a turn that runs: its process is killed, which ends the turn, and a warm-up or turn
    /// that comes after this starts none.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        Stop();
    }

    /// <summary>The command line, the model aside: see the class summary for why each is there.</summary>
    internal IReadOnlyList<string> Arguments(string model) =>
    [
        "-p",
        "--input-format", "stream-json",
        "--output-format", "stream-json",
        "--verbose",
        "--include-partial-messages",
        "--model", model,
        "--mcp-config", _paths.McpConfigFile,
        "--strict-mcp-config",
        "--tools", "",
        "--allowedTools", AllowedTools,
        "--permission-mode", "dontAsk",
        "--settings", NoHooks,
        "--no-session-persistence",
        "--system-prompt", BrainSettings.SystemPrompt,
    ];

    /// <summary>Starts the process when none runs or its model is not the one set; null when one runs, else why none could start.</summary>
    private string? EnsureRunning()
    {
        if (_disposed)
        {
            return "Raven's brain has shut down with CodeSwitchX.";
        }

        var model = _settings.Model;
        if (_process is { } running)
        {
            if (running.Exited.IsCompleted)
            {
                Lose(running, $"stopped (exit code {running.Exited.Result})");
            }
            else if (_processModel == model && _time.GetUtcNow() - _lastTurnAt < QuietReset)
            {
                return null;
            }
            else if (_processModel == model)
            {
                Stop(); // quiet long enough: a new conversation, without a word about it
            }
            else
            {
                Stop();
                _notices.Add(new BrainNotice($"Raven now thinks with {model}, starting a new conversation.", Warning: false));
            }
        }

        string? claude;
        try
        {
            claude = _findClaude();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "Could not look for Claude Code");
            return $"Raven could not look for Claude Code: {ex.Message}";
        }

        if (claude is null)
        {
            return "Claude Code is not installed, so Raven cannot answer. Install it (claude.ai/code) and ask again.";
        }

        if (!File.Exists(_paths.McpConfigFile))
        {
            return $"Raven cannot see the Yard: CodeSwitchX's MCP server did not start ({_paths.McpConfigFile} is missing). Restart CodeSwitchX.";
        }

        try
        {
            Directory.CreateDirectory(_paths.RavenDirectory);
            _process = _launcher.Start(claude, Arguments(model), _paths.RavenDirectory);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not start Raven's brain from {Claude}", claude);
            return $"Raven's brain could not be started from {claude}: {ex.Message}";
        }

        if (_disposed)
        {
            // Disposed while it started: the process it got would be owned by nothing.
            Stop();
            return "Raven's brain has shut down with CodeSwitchX.";
        }

        _processModel = model;
        _lastTurnAt = _time.GetUtcNow();
        _logger.LogInformation("Started Raven's brain: {Claude} with {Model}", claude, model);
        if (_lost is not null)
        {
            _notices.Add(new BrainNotice($"Raven's brain {_lost} and was started again. It has forgotten the conversation so far.", Warning: false));
            _lost = null;
        }

        return null;
    }

    private async Task<bool> SendAsync(IBrainProcess process, string text, CancellationToken ct)
    {
        var line = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
        }.ToJsonString();
        try
        {
            await process.WriteLineAsync(line, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Interrupts the running turn (a control request, as the Agent SDK sends it) and reads it to its <c>result</c>, which
    /// is not news. True once it ended; false when it did not within <see cref="InterruptTimeout"/>, or the process went.
    /// </summary>
    private async Task<bool> InterruptAsync(IBrainProcess process)
    {
        var line = new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = $"interrupt-{Interlocked.Increment(ref _interrupts)}",
            ["request"] = new JsonObject { ["subtype"] = "interrupt" },
        }.ToJsonString();
        try
        {
            using var timeout = new CancellationTokenSource(InterruptTimeout, _time);
            await process.WriteLineAsync(line, timeout.Token).ConfigureAwait(false);
            while (true)
            {
                var next = await process.Lines.ReadAsync(timeout.Token).ConfigureAwait(false);
                if (ClaudeStream.Read(next) is ClaudeTurnOver)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Threading.Channels.ChannelClosedException or IOException
            or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogWarning("Raven's brain did not end an interrupted turn; it is stopped");
            return false;
        }
    }

    /// <summary>The next line; null once the process closed its output; timed out after <see cref="Silence"/>.</summary>
    private async Task<(string? Line, bool TimedOut)> NextLineAsync(IBrainProcess process, CancellationToken ct)
    {
        try
        {
            return (await process.Lines.ReadAsync(ct).AsTask().WaitAsync(Silence, _time, ct).ConfigureAwait(false), false);
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
            return (null, false);
        }
        catch (TimeoutException)
        {
            return (null, true);
        }
    }

    /// <summary>
    /// The process went in the middle of a turn: the next turn starts a new one and says so. Waits a moment for the exit
    /// code, which comes a little after the output closes. Returns what the turn tells the user.
    /// </summary>
    private async Task<string> LoseAsync(IBrainProcess process, string what)
    {
        var exit = await Task.WhenAny(process.Exited, Task.Delay(TimeSpan.FromSeconds(2), _time)).ConfigureAwait(false) == process.Exited
            ? $" (exit code {process.Exited.Result})"
            : "";
        Lose(process, what + exit);
        return $"Raven's brain {what}{exit}. Ask again.";
    }

    private void Lose(IBrainProcess process, string why)
    {
        _logger.LogWarning("Raven's brain {Why}. Its last errors: {Errors}", why, process.ErrorTail);
        _lost = why;
        Stop();
    }

    /// <summary>Takes the process in one step: <see cref="Dispose"/> can stop it from another thread while a turn loses it.</summary>
    private void Stop()
    {
        var process = Interlocked.Exchange(ref _process, null);
        _processModel = null;
        process?.Dispose();
    }

    /// <summary>
    /// What the user is told when the brain's init line says the Yard's tools failed; null when they are connected, and
    /// empty (nothing to say yet) while they are still being connected to.
    /// </summary>
    private static string? YardProblem(ClaudeInit init) => init.McpServers.TryGetValue(YardMcp.ServerName, out var status)
        ? status switch
        {
            "connected" => null,
            "pending" => "",
            _ => $"Raven cannot see the Yard: its tools did not connect ({status}). Its answers can only guess.",
        }
        : "Raven cannot see the Yard: its tools are missing. Its answers can only guess.";
}
