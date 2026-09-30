using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Yard;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Conductor;

/// <summary>
/// Raven's brain on Claude Code: one long-lived <c>claude -p</c> that takes the turns as stream-json on its standard input,
/// so the conversation carries on from turn to turn. It can only look: no built-in tools at all, the Yard's MCP tools
/// from <c>mcp.json</c> and no others, anything not allowed denied without asking, and a working folder that is no
/// repository. None of the user's settings are loaded, so their hooks (the Yard's own, RAIVEN's) do not fire for its
/// turns, and nothing of it is saved as a session. A process that dies is started again for the next turn, which says so;
/// one whose model no longer is the one set is replaced.
/// </summary>
public sealed class ClaudeCliBrain : IConductorBrain
{
    /// <summary>How long a turn waits for the next line before it gives the process up: a tool call into the app takes milliseconds.</summary>
    public static readonly TimeSpan Silence = TimeSpan.FromSeconds(90);

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
    private bool _yardChecked;

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
                    case ClaudeInit init when !_yardChecked:
                        _yardChecked = true;
                        if (YardProblem(init) is { } problem)
                        {
                            yield return new BrainNotice(problem, Warning: true);
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
            // Cancelled, or left before the turn was over: the rest of its lines would be read as the next turn's.
            if (!finished && process is not null && ReferenceEquals(process, _process))
            {
                Stop();
            }

            _turns.Release();
        }
    }

    public void WarmUp() => _ = Task.Run(async () =>
    {
        try
        {
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
        Stop();
        return ValueTask.CompletedTask;
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
        "--setting-sources", "",
        "--no-session-persistence",
        "--system-prompt", BrainSettings.SystemPrompt,
    ];

    /// <summary>Starts the process when none runs or its model is not the one set; null when one runs, else why none could start.</summary>
    private string? EnsureRunning()
    {
        var model = _settings.Model;
        if (_process is { } running)
        {
            if (running.Exited.IsCompleted)
            {
                Lose(running, $"stopped (exit code {running.Exited.Result})");
            }
            else if (_processModel == model)
            {
                return null;
            }
            else
            {
                Stop();
                _notices.Add(new BrainNotice($"Raven now thinks with {model}, starting a new conversation.", Warning: false));
            }
        }

        if (_findClaude() is not { } claude)
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

        _processModel = model;
        _yardChecked = false;
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

    private void Stop()
    {
        var process = _process;
        _process = null;
        _processModel = null;
        process?.Dispose();
    }

    /// <summary>What the user is told when the brain's init line says the Yard's tools did not connect; null when they did.</summary>
    private static string? YardProblem(ClaudeInit init) => init.McpServers.TryGetValue(YardMcp.ServerName, out var status)
        ? status == "connected" ? null : $"Raven cannot see the Yard: its tools did not connect ({status}). Its answers can only guess."
        : "Raven cannot see the Yard: its tools are missing. Its answers can only guess.";
}
