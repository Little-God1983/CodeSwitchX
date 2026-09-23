using CodeSwitchX.Core;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Paths;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Ingest.Transcripts;

/// <summary>
/// Tails every <c>*.jsonl</c> under <c>~/.claude/projects</c> from its stored byte offset and publishes
/// <see cref="TranscriptUpdated"/> with titles, usage deltas, an inferred state signal and the new cursor.
/// The cursor is persisted by the <c>PersistenceWriter</c> in the same transaction as the usage, never here.
/// No failure of a single tick, folder or file ends the loop: indexing must survive Claude Code's cleanup deleting
/// folders mid-scan, files pending deletion and a locked database.
/// </summary>
public sealed class TranscriptIndexer : BackgroundService
{
    private readonly ClaudeCodePaths _claude;
    private readonly IUsageStore _cursorStore;
    private readonly IEventBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<TranscriptIndexer> _logger;
    private readonly TranscriptIndexerOptions _options;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly Dictionary<string, FileState> _files = new(StringComparer.Ordinal);
    private bool _cursorsLoaded;
    private FileSystemWatcher? _watcher;
    private volatile bool _dirty = true;

    public TranscriptIndexer(ClaudeCodePaths claude, IUsageStore cursorStore, IEventBus bus, TimeProvider time,
        ILogger<TranscriptIndexer> logger, TranscriptIndexerOptions options)
    {
        _claude = claude;
        _cursorStore = cursorStore;
        _bus = bus;
        _time = time;
        _logger = logger;
        _options = options;
    }

    /// <summary>Claude Code writes sub-agent transcripts as <c>agent-*.jsonl</c> (in newer versions under a <c>subagents</c> folder).</summary>
    internal static bool IsSubagentTranscript(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith("agent-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        return directory.Split('\\', '/').Any(segment => string.Equals(segment, "subagents", StringComparison.OrdinalIgnoreCase));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartWatcher();
        using var timer = new PeriodicTimer(_options.ScanInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (!_dirty && _watcher is not null)
                {
                    continue;
                }

                _dirty = false;
                try
                {
                    await ScanAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A bad tick must never end indexing for the rest of the process lifetime.
                    _logger.LogError(ex, "Transcript scan failed; retrying on the next tick");
                    _dirty = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One pass over every transcript. Never throws for I/O or store failures; it re-arms itself instead.</summary>
    internal async Task ScanAsync(CancellationToken ct)
    {
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await TryLoadCursorsAsync(ct).ConfigureAwait(false))
            {
                _dirty = true;
                return;
            }

            if (!Directory.Exists(_claude.ProjectsDirectory))
            {
                return;
            }

            List<string> files;
            try
            {
                // Materialise first: Claude Code's cleanupPeriodDays can delete a project folder mid-enumeration.
                files = Directory.EnumerateFiles(_claude.ProjectsDirectory, "*.jsonl",
                    new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Enumerating {Dir} failed; retrying on the next scan", _claude.ProjectsDirectory);
                _dirty = true;
                return;
            }

            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    ProcessFile(path);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Transcript {Path} could not be indexed in this pass", path);
                    _dirty = true;
                }
            }
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task<bool> TryLoadCursorsAsync(CancellationToken ct)
    {
        if (_cursorsLoaded)
        {
            return true;
        }

        IReadOnlyList<TranscriptCursor> cursors;
        try
        {
            cursors = await _cursorStore.GetCursorsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Transcript cursors could not be loaded; retrying on the next scan");
            return false;
        }

        foreach (var cursor in cursors)
        {
            _files[cursor.Path] = new FileState(_options.MessageIdMemory)
            {
                Offset = cursor.ByteOffset,
                LastWriteUtc = cursor.LastWriteUtc,
                SessionId = cursor.SessionId,
                TitleReported = true,
            };
        }

        _cursorsLoaded = true;
        return true;
    }

    private void ProcessFile(string path)
    {
        var key = PathNormalizer.Normalize(path);
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Transcript {Path} is not accessible right now", path);
            return;
        }

        if (!_files.TryGetValue(key, out var state))
        {
            state = new FileState(_options.MessageIdMemory);
            _files[key] = state;
        }

        if (info.Length == state.Offset && state.Offset > 0)
        {
            return;
        }

        TailResult tail;
        try
        {
            tail = TranscriptTailer.ReadNewLines(path, state.Offset, _options.MaxBytesPerPass);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Transcript {Path} is not readable right now", path);
            return;
        }

        if (tail.Truncated)
        {
            _logger.LogInformation("Transcript {Path} shrank below the stored offset; re-reading from the start", path);
            state.Reset();
        }

        if (tail.HasMore)
        {
            _dirty = true;
        }

        if (tail.Lines.Count == 0 && tail.NewOffset == state.Offset)
        {
            return;
        }

        var lastWriteUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        var historical = _time.GetUtcNow() - lastWriteUtc > _options.HistoryWindow;
        var update = BuildUpdate(path, state, tail.Lines, historical, IsSubagentTranscript(path));
        state.Offset = tail.NewOffset;
        state.LastWriteUtc = lastWriteUtc;

        var cursor = new TranscriptCursor { Path = key, ByteOffset = state.Offset, LastWriteUtc = state.LastWriteUtc, SessionId = state.SessionId };
        _bus.Publish(new TranscriptUpdated(update with { Cursor = cursor }));
    }

    private TranscriptUpdate BuildUpdate(string path, FileState state, IReadOnlyList<string> lines, bool historical, bool subagent)
    {
        var usage = new List<UsageDelta>();
        string? newTitle = null;
        var summaryTitle = false;
        string? cwd = null;
        string? model = null;
        DateTimeOffset? lastActivity = null;
        TokenUsage? latestContext = null;
        var pendingToolUse = state.PendingToolUse;

        foreach (var raw in lines)
        {
            var line = TranscriptLineParser.TryParse(raw);
            if (line is null)
            {
                _logger.LogDebug("Skipping unparsable line in {Path}", path);
                continue;
            }

            state.SessionId ??= line.SessionId;
            cwd ??= line.Cwd;
            if (line.Timestamp is { } ts && (lastActivity is null || ts > lastActivity))
            {
                lastActivity = ts;
            }

            switch (line)
            {
                case AssistantLine assistant:
                    model = assistant.Model ?? model;
                    if (assistant.HasToolUse)
                    {
                        pendingToolUse = true;
                    }

                    if (assistant.Usage is { } tokens && assistant.MessageId is { } id && state.RememberMessage(id))
                    {
                        usage.Add(new UsageDelta(assistant.Model ?? model ?? "unknown", assistant.Timestamp ?? _time.GetUtcNow(), tokens));
                        latestContext = tokens;
                    }
                    else if (assistant.Usage is { } sameMessage)
                    {
                        latestContext = sameMessage;
                    }

                    break;

                case UserLine user:
                    if (user.IsToolResult)
                    {
                        pendingToolUse = false;
                    }
                    else if (!user.IsMeta && user.Text is not null)
                    {
                        pendingToolUse = false;
                        if (!state.TitleReported && !summaryTitle && newTitle is null)
                        {
                            newTitle = ChatTitle.FromPrompt(user.Text);
                        }
                    }

                    break;

                case SummaryLine summary:
                    if (!state.HasSummary)
                    {
                        newTitle = ChatTitle.FromPrompt(summary.Title);
                        summaryTitle = true;
                        state.HasSummary = true;
                    }

                    break;
            }
        }

        state.PendingToolUse = pendingToolUse;
        if (newTitle is not null)
        {
            state.TitleReported = true;
        }

        var sessionId = state.SessionId ?? Path.GetFileNameWithoutExtension(path);
        state.SessionId = sessionId;
        var now = _time.GetUtcNow();

        if (subagent)
        {
            // A sub-agent's prompts, tool calls and context window belong to the sub-agent, not the parent chat;
            // only its token usage counts towards the parent session.
            return new TranscriptUpdate
            {
                SessionId = sessionId,
                TranscriptPath = path,
                ObservedAt = now,
                Model = model,
                Usage = usage,
                Historical = historical,
            };
        }

        // Spec: a write within the working window means Working; otherwise a trailing assistant tool call
        // without its result means Waiting (best effort, e.g. a permission prompt); otherwise Idle.
        var recentlyWritten = lastActivity is { } last && now - last <= _options.WorkingWindow;
        var inferred = recentlyWritten ? SessionSignal.ToolUse
            : pendingToolUse ? SessionSignal.Notification
            : SessionSignal.Stop;

        return new TranscriptUpdate
        {
            SessionId = sessionId,
            TranscriptPath = path,
            ObservedAt = now,
            Title = newTitle,
            Cwd = cwd,
            Model = model,
            LastActivityAt = lastActivity,
            Usage = usage,
            LatestContext = latestContext,
            InferredSignal = inferred,
            PendingToolUse = pendingToolUse,
            Historical = historical,
        };
    }

    private void StartWatcher()
    {
        try
        {
            Directory.CreateDirectory(_claude.ProjectsDirectory);
            _watcher = new FileSystemWatcher(_claude.ProjectsDirectory, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Changed += (_, _) => _dirty = true;
            _watcher.Created += (_, _) => _dirty = true;
            _watcher.Renamed += (_, _) => _dirty = true;
            _watcher.Error += (_, e) =>
            {
                _logger.LogWarning(e.GetException(), "Transcript watcher error; falling back to periodic scans");
                _dirty = true;
            };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "Cannot watch {Dir}; using periodic scans only", _claude.ProjectsDirectory);
            _watcher = null;
        }
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        _scanGate.Dispose();
        base.Dispose();
    }

    private sealed class FileState(int memory)
    {
        private readonly Queue<string> _recent = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public long Offset { get; set; }
        public DateTimeOffset LastWriteUtc { get; set; }
        public string? SessionId { get; set; }
        public bool TitleReported { get; set; }
        public bool HasSummary { get; set; }
        public bool PendingToolUse { get; set; }

        /// <returns>True when the id was not seen before.</returns>
        public bool RememberMessage(string id)
        {
            if (!_seen.Add(id))
            {
                return false;
            }

            _recent.Enqueue(id);
            while (_recent.Count > memory)
            {
                _seen.Remove(_recent.Dequeue());
            }

            return true;
        }

        public void Reset()
        {
            Offset = 0;
            _recent.Clear();
            _seen.Clear();
            TitleReported = false;
            HasSummary = false;
            PendingToolUse = false;
        }
    }
}
