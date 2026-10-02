using System.Text.Json.Nodes;
using CodeSwitchX.Core.Yard;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Conductor;

/// <summary>
/// Runs Raven's chats on Claude Code: <c>claude -p</c> with stream-json both ways, one long-lived process per chat that
/// takes each of the user's turns on its standard input. It runs in <c>--permission-mode auto</c>, Claude Code's own
/// default, with the user's settings and hooks, so the Yard sees it through the hooks like any chat. The chat id is set
/// up front with <c>--session-id</c>, and <c>CLAUDE_CODE_ENTRYPOINT=claude-vscode</c> marks it as VS Code's: the
/// extension hides sessions a plain <c>claude -p</c> writes, and would not open it when the user takes it over.
/// </summary>
public sealed class ClaudeAgentLauncher : IAgentLauncher
{
    /// <summary>How long a start waits for the chat to begin answering before it counts it as started anyway.</summary>
    public static readonly TimeSpan StartWait = TimeSpan.FromSeconds(15);

    /// <summary>How long a chat between turns is given to end by itself once its input is closed, before it is killed.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(3);

    /// <summary>The entry point the VS Code extension itself sets; it opens only sessions that carry one of its own.</summary>
    internal const string VsCodeEntryPoint = "claude-vscode";

    private readonly IBrainProcessLauncher _launcher;
    private readonly Func<string?> _findClaude;
    private readonly TimeProvider _time;
    private readonly ILogger<ClaudeAgentLauncher> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Worker> _workers = new(StringComparer.Ordinal);
    private volatile bool _disposed;

    public ClaudeAgentLauncher(IBrainProcessLauncher launcher, Func<string?> findClaude, TimeProvider time, ILogger<ClaudeAgentLauncher> logger)
    {
        _launcher = launcher;
        _findClaude = findClaude;
        _time = time;
        _logger = logger;
    }

    public event Action<AgentChat>? Changed;

    public event Action<AgentChat, string>? Failed;

    public IReadOnlyList<AgentChat> Chats
    {
        get
        {
            lock (_gate)
            {
                return _workers.Values.Select(w => w.Snapshot()).ToList();
            }
        }
    }

    public AgentChat? Find(string idOrPrefix) => FindWorker(idOrPrefix)?.Snapshot();

    public async Task<AgentStart> StartAsync(AgentRequest request, CancellationToken ct)
    {
        // Off the caller's thread: looking for claude.exe and starting it touch the disk.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (_disposed)
        {
            throw new YardActionException("CodeSwitchX is closing, so no chat can be started.");
        }

        if (!Directory.Exists(request.Folder))
        {
            throw new YardActionException($"The folder {request.Folder} is not there any more.");
        }

        string? claude;
        try
        {
            claude = _findClaude();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new YardActionException($"Raven could not look for Claude Code: {ex.Message}");
        }

        if (claude is null)
        {
            throw new YardActionException("Claude Code is not installed, so no chat can be started. Install it (claude.ai/code).");
        }

        IBrainProcess process;
        try
        {
            process = _launcher.Start(claude, Arguments(request), request.Folder, Environment);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not start a chat from {Claude}", claude);
            throw new YardActionException($"Claude Code could not be started from {claude}: {ex.Message}");
        }

        var worker = new Worker(request, process);
        worker.LineSent();
        lock (_gate)
        {
            _workers[request.Id] = worker;
        }

        _logger.LogInformation("Started chat {Id} in {Folder} with {Model} at {Effort} effort", request.Id, request.Folder,
            request.Model ?? "the default model", request.Effort ?? "the default");
        worker.Pump = PumpAsync(worker);
        Raise(worker);

        string? failure;
        try
        {
            if (!await SendLineAsync(process, request.Prompt, ct).ConfigureAwait(false))
            {
                failure = "Claude Code stopped before it took the prompt.";
            }
            else
            {
                try
                {
                    failure = await worker.FirstSign.Task.WaitAsync(StartWait, _time, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Still thinking: under way, unless the chat said otherwise just now.
                    worker.FirstSign.TrySetResult(null);
                    failure = await worker.FirstSign.Task.ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // The caller gave up (the brain's turn was stopped): it was told the start failed, so the chat must not go
            // on working in the folder, unseen, while the brain starts another for the same task.
            await StopWorkerAsync(worker).ConfigureAwait(false);
            throw;
        }

        if (failure is not null)
        {
            await StopWorkerAsync(worker).ConfigureAwait(false);
            return new AgentStart(worker.Snapshot(), failure);
        }

        return new AgentStart(worker.Snapshot(), null);
    }

    public async Task<AgentChat> SendAsync(string chatId, string text, CancellationToken ct)
    {
        var worker = FindWorker(chatId) ?? throw NotOurs(chatId);
        worker.LineSent();
        var sent = false;
        try
        {
            sent = await SendLineAsync(worker.Process, text, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!sent)
            {
                worker.LineUnsent(); // cancelled or failed: it will not be taken, and must not keep the chat working
            }
        }

        if (!sent)
        {
            throw new YardActionException("That chat has stopped, so it cannot take anything more.");
        }

        Raise(worker);
        return worker.Snapshot();
    }

    public async Task<AgentChat> StopAsync(string chatId, CancellationToken ct)
    {
        var worker = FindWorker(chatId) ?? throw NotOurs(chatId);
        var before = worker.Snapshot();
        await StopWorkerAsync(worker).WaitAsync(ct).ConfigureAwait(false);
        return before;
    }

    /// <summary>Kills every chat: the app exits, and a chat it started cannot run without it.</summary>
    public void Dispose()
    {
        _disposed = true;
        List<Worker> workers;
        lock (_gate)
        {
            workers = [.. _workers.Values];
        }

        foreach (var worker in workers)
        {
            worker.StoppedByUs = true;
            worker.Process.Dispose();
        }
    }

    internal static IReadOnlyList<string> Arguments(AgentRequest request)
    {
        List<string> arguments =
        [
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--replay-user-messages",
            "--permission-mode", "auto",
            "--session-id", request.Id,
        ];
        if (request.Model is { Length: > 0 } model)
        {
            arguments.AddRange(["--model", model]);
        }

        if (request.Effort is { Length: > 0 } effort)
        {
            arguments.AddRange(["--effort", effort]);
        }

        return arguments;
    }

    internal static readonly IReadOnlyDictionary<string, string?> Environment = new Dictionary<string, string?>
    {
        ["CLAUDE_CODE_ENTRYPOINT"] = VsCodeEntryPoint,
    };

    /// <summary>
    /// Reads the chat's output as long as it runs: its mode, the lines it takes, when a turn begins, begins to answer and is
    /// over. Standard output must be read all the time anyway, or a full pipe would stop the process.
    /// </summary>
    private async Task PumpAsync(Worker worker)
    {
        try
        {
            await foreach (var line in worker.Process.Lines.ReadAllAsync().ConfigureAwait(false))
            {
                switch (ClaudeStream.Read(line))
                {
                    case ClaudeInit init:
                        worker.PermissionMode = init.PermissionMode;
                        worker.TurnBegun();
                        Raise(worker);
                        break;
                    case ClaudeTaken:
                        worker.LineTaken();
                        break;
                    case ClaudeTurnOver over:
                        worker.TurnOver();
                        worker.FirstSign.TrySetResult(over.Error is null ? null : $"The chat could not start: {over.Error}");
                        if (over.Error is { } error)
                        {
                            _logger.LogWarning("Chat {Id} failed a turn: {Error}", worker.Id, error);
                            if (worker.Begun)
                            {
                                Failed?.Invoke(worker.Snapshot(), error);
                            }
                        }

                        Raise(worker);
                        break;
                    case ClaudeAnswer or ClaudeEvents:
                        worker.FirstSign.TrySetResult(null);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading chat {Id} failed", worker.Id);
        }

        var exited = await Task.WhenAny(worker.Process.Exited, Task.Delay(TimeSpan.FromSeconds(2), _time)).ConfigureAwait(false) == worker.Process.Exited;
        var why = exited ? $"stopped (exit code {worker.Process.Exited.Result})" : "stopped";
        worker.End();
        lock (_gate)
        {
            _workers.Remove(worker.Id);
        }

        worker.FirstSign.TrySetResult($"Claude Code {why} before the chat began.");
        if (!worker.StoppedByUs)
        {
            _logger.LogWarning("Chat {Id} {Why}. Its last errors: {Errors}", worker.Id, why, worker.Process.ErrorTail);
            if (worker.Begun)
            {
                Failed?.Invoke(worker.Snapshot(), $"Claude Code {why}.");
            }
        }

        worker.Process.Dispose();
        Raise(worker);
    }

    /// <summary>Ends the chat as Claude Code ends a session when it is between turns, and kills it otherwise; waits until it is gone.</summary>
    private async Task StopWorkerAsync(Worker worker)
    {
        worker.StoppedByUs = true;
        if (!worker.Working)
        {
            worker.Process.CloseInput();
            await Task.WhenAny(worker.Process.Exited, Task.Delay(StopWait, _time)).ConfigureAwait(false);
        }

        worker.Process.Dispose();
        if (worker.Pump is { } pump)
        {
            await Task.WhenAny(pump, Task.Delay(StopWait, _time)).ConfigureAwait(false);
        }
    }

    private static async Task<bool> SendLineAsync(IBrainProcess process, string text, CancellationToken ct)
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

    private Worker? FindWorker(string idOrPrefix)
    {
        var key = (idOrPrefix ?? "").Trim();
        if (key.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (_workers.TryGetValue(key, out var exact))
            {
                return exact;
            }

            var found = _workers.Values.Where(w => w.Id.StartsWith(key, StringComparison.OrdinalIgnoreCase)).ToList();
            return found.Count == 1 ? found[0] : null;
        }
    }

    private static YardActionException NotOurs(string chatId) => new(
        $"No chat Raven started runs with the id '{chatId}'. Only those take send_to_chat and stop_chat; a chat running in VS Code is told with SendMessage, to its send_to name from list_chats.");

    /// <summary>
    /// Tells of the chat as it is now. The start, a send and the pump raise from their own threads: one at a time per
    /// chat, so the listener gets them in the order they were taken, and the last one is never older than the end.
    /// </summary>
    private void Raise(Worker worker)
    {
        lock (worker.Gate)
        {
            Changed?.Invoke(worker.Snapshot());
        }
    }

    private sealed class Worker(AgentRequest request, IBrainProcess process)
    {
        private volatile int _unread;
        private volatile bool _inTurn;
        private bool _echoes;

        public Lock Gate { get; } = new();

        public string Id => request.Id;

        public IBrainProcess Process { get; } = process;

        /// <summary>Null once the chat has begun to answer, or why its first turn failed.</summary>
        public TaskCompletionSource<string?> FirstSign { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? Pump { get; set; }

        /// <summary>
        /// The chat began: its start says, or has said, it started, so failures from here on are news for the user. A
        /// failure that settles the first sign itself is the start's to tell.
        /// </summary>
        public bool Begun => FirstSign.Task is { IsCompletedSuccessfully: true, Result: null };

        /// <summary>The app stopped it: its end is no failure.</summary>
        public volatile bool StoppedByUs;

        /// <summary>
        /// A line written to it is not taken yet, or a turn runs. Not a count of lines against <c>result</c>s: Claude Code
        /// folds a line sent during a tool call into the running turn, and starts a turn by itself when a background task
        /// ends (seen with CLI 2.1.286). Each turn begins with an <c>init</c>, and each line is echoed as it is taken.
        /// </summary>
        public bool Working => _unread > 0 || _inTurn;

        public volatile string? PermissionMode;

        public volatile bool Ended;

        public void LineSent()
        {
            lock (Gate)
            {
                _unread++;
            }
        }

        public void LineUnsent()
        {
            lock (Gate)
            {
                _unread = Math.Max(0, _unread - 1);
            }
        }

        public void LineTaken()
        {
            lock (Gate)
            {
                _echoes = true;
                _unread = Math.Max(0, _unread - 1);
                _inTurn = true;
            }
        }

        public void TurnBegun()
        {
            lock (Gate)
            {
                _inTurn = true;
            }
        }

        public void TurnOver()
        {
            lock (Gate)
            {
                _inTurn = false;
                if (!_echoes)
                {
                    _unread = 0; // a Claude Code that does not echo the lines it takes: no line would ever count as taken
                }
            }
        }

        public void End()
        {
            lock (Gate)
            {
                _unread = 0;
                _inTurn = false;
                Ended = true;
            }
        }

        public AgentChat Snapshot() => new(request.Id, request.WorkspaceId, request.Workspace, request.Folder, request.Model, request.Effort, Working,
            PermissionMode, Ended, Ended && StoppedByUs);
    }
}
