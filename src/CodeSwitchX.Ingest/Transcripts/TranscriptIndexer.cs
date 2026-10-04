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
/// folders mid-scan, files pending deletion and a locked database. A file that cannot be read is tried again by itself.
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

    /// <summary>Files a pass could not read, each with its next attempt. Touched only under <see cref="_scanGate"/>.</summary>
    private readonly Dictionary<string, Retry> _retries = new(StringComparer.Ordinal);
    private readonly MessageMemory _messages;
    private bool _cursorsLoaded;
    private FileSystemWatcher? _watcher;
    private volatile bool _watching;
    private volatile bool _dirty = true;

    /// <summary>Whether the next scan asks the file system about every file instead of trusting the enumeration (see <see cref="Index"/>).</summary>
    private volatile bool _refreshAll = true;

    /// <summary>Files a scan did not enumerate but found in place; looked for again once a minute, not on every scan. Touched only under <see cref="_scanGate"/>.</summary>
    private readonly HashSet<string> _parked = new(StringComparer.Ordinal);
    private DateTimeOffset _parkedLookedAt = DateTimeOffset.MinValue;

    /// <summary>Claude Code records API errors as assistant lines with this model and zero usage; they carry no model, context or tokens.</summary>
    internal const string SyntheticModel = "<synthetic>";

    /// <summary>Whether the next tick scans everything: set by the folder watcher, its refresh, and a pass that could not run at all.</summary>
    internal bool Dirty => _dirty;

    /// <summary>When a file an earlier pass could not read is tried again, or null once it was read or is gone.</summary>
    internal DateTimeOffset? NextAttempt(string path) => _retries.GetValueOrDefault(PathNormalizer.Normalize(path))?.DueAt;

    public TranscriptIndexer(ClaudeCodePaths claude, IUsageStore cursorStore, IEventBus bus, TimeProvider time,
        ILogger<TranscriptIndexer> logger, TranscriptIndexerOptions options)
    {
        _claude = claude;
        _cursorStore = cursorStore;
        _bus = bus;
        _time = time;
        _logger = logger;
        _options = options;
        _messages = new MessageMemory(options.MessageIdMemory);
    }

    /// <summary>Claude Code writes sub-agent transcripts as <c>agent-*.jsonl</c> (in newer versions under a <c>subagents</c> folder).</summary>
    internal static bool IsSubagentTranscript(string path) =>
        Path.GetFileName(path).StartsWith("agent-", StringComparison.OrdinalIgnoreCase) || SessionFolder(path) is not null;

    /// <summary>
    /// The session folder that holds a <c>subagents</c> folder (<c>&lt;project&gt;/&lt;session&gt;/subagents/...</c>), or null
    /// when the path is not under one. It names the session of a file whose lines do not, such as a workflow's <c>journal.jsonl</c>.
    /// </summary>
    private static string? SessionFolder(string path)
    {
        var segments = (Path.GetDirectoryName(path) ?? string.Empty).Split('\\', '/');
        var index = Array.FindLastIndex(segments, segment => string.Equals(segment, "subagents", StringComparison.OrdinalIgnoreCase));
        return index > 0 ? segments[index - 1] : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartWatcher();
        var watcherStartedAt = _time.GetUtcNow();
        using var timer = new PeriodicTimer(_options.ScanInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                // A watcher can stop without an error (its folder renamed away and a new one created in its place), and after
                // an error the loop polls: either way a fresh watcher and a full scan put it right.
                if (_time.GetUtcNow() - watcherStartedAt >= _options.WatcherRefreshInterval)
                {
                    var stale = _watcher;
                    StartWatcher();
                    stale?.Dispose();
                    watcherStartedAt = _time.GetUtcNow();
                    _refreshAll = true;
                    _dirty = true;
                }

                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// One timer tick: a pass over every transcript when the watcher reported a change, or without a running watcher;
    /// otherwise only the files an earlier pass could not read, once their next attempt is due.
    /// </summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        var everything = _dirty || !_watching;
        if (everything)
        {
            _dirty = false;
        }

        try
        {
            if (everything)
            {
                await ScanAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await RetryAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
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

            List<FileInfo> files;
            try
            {
                // Materialise first: Claude Code's cleanupPeriodDays can delete a project folder mid-enumeration. The entries
                // bring each file's size and write time along; asking the file system again per file doubled a scan's cost.
                files = new DirectoryInfo(_claude.ProjectsDirectory).EnumerateFiles("*.jsonl",
                    new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Enumerating {Dir} failed; retrying on the next scan", _claude.ProjectsDirectory);
                _dirty = true;
                return;
            }

            // Without a watcher every scan is the only way to notice a write; with one, the minute refresh bounds what an
            // event that never came (see Index) can hide, as the second look per file did before.
            var refreshAll = _refreshAll || !_watching;
            _refreshAll = false;
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                found.Add(ProcessFile(file, refreshAll));
            }

            ForgetGone(found);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>
    /// Forgets the transcripts a scan no longer finds: Claude Code's cleanupPeriodDays deletes old transcripts, and without
    /// this every start loads every cursor ever stored. The rows go through the writer (<see cref="TranscriptsForgotten"/>),
    /// in order with the cursors it still holds for the same files, so none is written back after its row went. A missing
    /// file is looked for in its folder's listing: a folder that cannot be listed (the enumeration skipped it, see
    /// IgnoreInaccessible) keeps its cursors, or its files would be read from the start, and their usage counted again,
    /// once it opens. A file found in place though the scan did not enumerate it (such a folder, or a projects folder
    /// that is no longer the configured one) is parked and looked for again once a minute, not on every scan.
    /// </summary>
    private void ForgetGone(HashSet<string> found)
    {
        var now = _time.GetUtcNow();
        var lookAtParked = now - _parkedLookedAt >= _options.WatcherRefreshInterval;
        var missing = _files.Keys.Where(key => !found.Contains(key) && (lookAtParked || !_parked.Contains(key))).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        if (lookAtParked)
        {
            _parkedLookedAt = now;
            _parked.Clear();
        }

        var gone = new List<string>();
        foreach (var folder in missing.GroupBy(key => Path.GetDirectoryName(key) ?? string.Empty, StringComparer.Ordinal))
        {
            HashSet<string> present;
            try
            {
                present = Directory.EnumerateFiles(folder.Key, "*.jsonl").Select(PathNormalizer.Normalize).ToHashSet(StringComparer.Ordinal);
            }
            catch (DirectoryNotFoundException)
            {
                gone.AddRange(folder);
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogDebug(ex, "Cannot look into {Folder}; its transcripts keep their cursors", folder.Key);
                _parked.UnionWith(folder);
                continue;
            }

            foreach (var key in folder)
            {
                if (present.Contains(key))
                {
                    _parked.Add(key);
                }
                else
                {
                    gone.Add(key);
                }
            }
        }

        if (gone.Count == 0)
        {
            return;
        }

        foreach (var key in gone)
        {
            _files.Remove(key);
            _retries.Remove(key);
        }

        _bus.Publish(new TranscriptsForgotten(gone));
        _logger.LogInformation("Forgot {Count} transcripts that no longer exist", gone.Count);
    }

    /// <summary>Reads again the files an earlier pass could not, once their next attempt is due. Nothing else is touched.</summary>
    private async Task RetryAsync(CancellationToken ct)
    {
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            foreach (var retry in _retries.Values.Where(r => r.DueAt <= now).ToList())
            {
                ct.ThrowIfCancellationRequested();
                ProcessFile(new FileInfo(retry.Path), refresh: false);
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
        IReadOnlyList<string> seenMessages;
        try
        {
            cursors = await _cursorStore.GetCursorsAsync(ct).ConfigureAwait(false);
            seenMessages = await _cursorStore.GetSeenMessageIdsAsync(_options.MessageIdMemory, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Transcript cursors could not be loaded; retrying on the next scan");
            return false;
        }

        // Oldest first, so the memory evicts in the same order it would have without the restart.
        foreach (var id in seenMessages)
        {
            _messages.Remember(id);
        }

        var readAgain = 0;
        foreach (var cursor in cursors)
        {
            // The CursorTitles migration marks a cursor from before titles were stored with a prompt source and no title:
            // the lines behind its offset were read without a look at their title lines (a /rename name among them), so the
            // file is read again from the start, as after a rewrite, which counts no usage twice.
            var beforeTitles = cursor.Title is null && cursor.TitleSource == TitleSource.Prompt;
            // A title stored before #118 can be the start of SendMessage's envelope, which names no task: it is no title,
            // and the file is read again the same way for the task in its first prompt.
            var envelope = cursor is { TitleSource: TitleSource.Prompt, Title: { } stored } && ChatTitle.FromPrompt(stored, int.MaxValue) is null;
            beforeTitles |= envelope;
            readAgain += beforeTitles ? 1 : 0;
            _files[cursor.Path] = new FileState
            {
                Offset = beforeTitles ? 0 : cursor.ByteOffset,
                CountedUntil = beforeTitles ? cursor.LastWriteUtc : null,
                LastWriteUtc = cursor.LastWriteUtc,
                SessionId = cursor.SessionId,
                Title = envelope ? null : cursor.Title,
                TitleSource = envelope ? TitleSource.None : cursor.TitleSource,
                // The chat engine may not have shown it (the chat was historical, or the app was closed before its next
                // live update): it goes out again with the first live update after the restart.
                UndeliveredTitle = envelope ? null : cursor.Title,
                LastCountedAt = cursor.LastWriteUtc,
            };
        }

        if (readAgain > 0)
        {
            _logger.LogInformation("Reading {Count} transcripts again for the titles stored since this version", readAgain);
        }

        _cursorsLoaded = true;
        return true;
    }

    /// <summary>
    /// Never throws: a file that cannot be read or indexed now is tried again by itself, later, not by a scan of everything.
    /// Returns the file's key.
    /// </summary>
    private string ProcessFile(FileInfo file, bool refresh)
    {
        var key = PathNormalizer.Normalize(file.FullName);
        try
        {
            Index(file, key, refresh);
            _retries.Remove(key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ScheduleRetry(key, file.FullName, ex);
        }

        return key;
    }

    private void ScheduleRetry(string key, string path, Exception ex)
    {
        // The next tick first (a sharing violation on a chat's last write clears at once), then twice the wait each time
        // up to the cap: a file that can never be read costs one failed open now and then, never a scan of everything.
        var attempts = (_retries.GetValueOrDefault(key)?.Attempts ?? 0) + 1;
        var delay = TimeSpan.FromTicks(Math.Min(_options.ScanInterval.Ticks << Math.Min(attempts - 1, 20), _options.MaxRetryDelay.Ticks));
        _retries[key] = new Retry(path, attempts, _time.GetUtcNow() + delay);
        if (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Transcript {Path} is not readable right now; trying again in {Delay}", path, delay);
        }
        else
        {
            _logger.LogWarning(ex, "Transcript {Path} could not be indexed in this pass; trying again in {Delay}", path, delay);
        }
    }

    /// <param name="info">From the scan's enumeration, with its size and write time, or fresh for a retry.</param>
    /// <param name="refresh">Ask the file system about this file whatever the enumeration says.</param>
    private void Index(FileInfo info, string key, bool refresh)
    {
        if (refresh || _time.GetUtcNow() - info.LastWriteTimeUtc <= _options.HistoryWindow)
        {
            // Windows reports a write through a handle still open only once it reaches the disk: until then the folder entry
            // the enumeration reads shows the old size and the old write time, and the watcher says nothing either. Asking the
            // file itself shows the write. The few files written within the history window are asked on every scan, every
            // file once a minute (the watcher refresh) and on every scan without a watcher; the thousands of old ones are
            // trusted in between, which is what makes a scan cheap.
            info.Refresh();
        }

        if (!info.Exists)
        {
            return;
        }

        var path = info.FullName;

        if (!_files.TryGetValue(key, out var state))
        {
            state = new FileState();
            _files[key] = state;
        }

        if (info.Length == state.Offset && state.Offset > 0)
        {
            return;
        }

        var tail = TranscriptTailer.ReadNewLines(path, state.Offset, _options.MaxBytesPerPass);
        if (tail.Truncated)
        {
            _logger.LogInformation("Transcript {Path} was rewritten; re-reading it from the start", path);
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
        var update = BuildUpdate(path, state, tail.Lines, historical, IsSubagentTranscript(path), partial: tail.HasMore);
        state.Offset = tail.NewOffset;
        state.LastWriteUtc = lastWriteUtc;

        var cursor = new TranscriptCursor
        {
            Path = key, ByteOffset = state.Offset, LastWriteUtc = state.LastWriteUtc, SessionId = state.SessionId,
            Title = state.Title, TitleSource = state.TitleSource,
        };
        _bus.Publish(new TranscriptUpdated(update with { Cursor = cursor }));
    }

    private TranscriptUpdate BuildUpdate(string path, FileState state, IReadOnlyList<string> lines, bool historical, bool subagent, bool partial)
    {
        var usage = new List<UsageDelta>();
        string? newTitle = null;
        string? cwd = null;
        string? model = null;
        DateTimeOffset? lastActivity = null;
        TokenUsage? latestContext = null;
        var pendingToolUse = state.PendingToolUse;
        var interrupted = state.CarriedInterrupt;
        var newMessageIds = new List<string>();

        // Claude Code's own order: a /rename name, then the generated title, then the first prompt. A title from a lower
        // source never replaces one from a higher (the first prompt stays until a title arrives, the first generated title
        // wins over later copies of it); only another /rename replaces a /rename name.
        void Offer(string? candidate, TitleSource source)
        {
            if (candidate is not null && (source > state.TitleSource
                || (source == TitleSource.Custom && !string.Equals(candidate, state.Title, StringComparison.Ordinal))))
            {
                newTitle = state.Title = candidate;
                state.TitleSource = source;
            }
        }

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
                    if (string.Equals(assistant.Model, SyntheticModel, StringComparison.Ordinal))
                    {
                        break; // an API error, not a reply: it must not become the model or zero the context
                    }

                    interrupted = false;
                    model = assistant.Model ?? model;
                    if (assistant.HasToolUse)
                    {
                        pendingToolUse = true;
                    }

                    // Dedup across every file and across restarts (the ids are saved with the usage): `claude --resume`
                    // replays earlier assistant messages, with their usage, into a new transcript. After a rewrite the file
                    // is read again, and what it held before was counted then (see FileState.Reset).
                    var countedBefore = assistant.Timestamp <= state.CountedUntil;
                    if (assistant.Usage is { } tokens && assistant.MessageId is { } id && !countedBefore && _messages.Remember(id))
                    {
                        usage.Add(new UsageDelta(assistant.Model ?? model ?? "unknown", assistant.Timestamp ?? _time.GetUtcNow(), tokens));
                        latestContext = tokens;
                        newMessageIds.Add(id);
                        if (assistant.Timestamp is { } at && (state.LastCountedAt is null || at > state.LastCountedAt))
                        {
                            state.LastCountedAt = at;
                        }
                    }
                    else if (assistant.Usage is { } sameMessage)
                    {
                        latestContext = sameMessage;
                    }

                    break;

                case UserLine user:
                    if (user.IsInterrupt)
                    {
                        // Esc: the turn is over and no tool result will come; Claude Code fires no Stop hook for this.
                        interrupted = true;
                        pendingToolUse = false;
                    }
                    else if (user.IsToolResult)
                    {
                        pendingToolUse = false;
                    }
                    else if (!user.IsMeta && user.Text is not null)
                    {
                        interrupted = false;
                        pendingToolUse = false;
                        if (!subagent)
                        {
                            Offer(ChatTitle.FromPrompt(user.Text), TitleSource.Prompt); // a sub-agent's prompt is no chat title
                        }
                    }

                    break;

                case SummaryLine summary when !subagent:
                    Offer(ChatTitle.FromPrompt(summary.Title), TitleSource.Generated);
                    break;

                case CustomTitleLine custom when !subagent:
                    var name = ChatTitle.FromPrompt(custom.Title);
                    if (state.SkipCustomUntilCurrent)
                    {
                        // A re-read after a rewrite: the names before the current one were seen, and the current one is kept.
                        state.SkipCustomUntilCurrent = !string.Equals(name, state.Title, StringComparison.Ordinal);
                    }
                    else
                    {
                        Offer(name, TitleSource.Custom);
                    }

                    break;
            }
        }

        state.PendingToolUse = pendingToolUse;
        state.CarriedInterrupt = partial && interrupted;
        if (!partial)
        {
            state.SkipCustomUntilCurrent = false; // the whole file was read again: a current name no longer in it is kept, later names count
        }

        // The chat engine drops a historical update of a chat it does not show, title and all, so a title found while the
        // transcript was historical goes out again with its first live update.
        string? title;
        if (historical)
        {
            state.UndeliveredTitle = newTitle ?? state.UndeliveredTitle;
            title = newTitle;
        }
        else
        {
            title = newTitle ?? state.UndeliveredTitle;
            state.UndeliveredTitle = null;
        }

        var sessionId = state.SessionId ?? (subagent ? SessionFolder(path) : null) ?? Path.GetFileNameWithoutExtension(path);
        state.SessionId = sessionId;
        var now = _time.GetUtcNow();
        var recentlyWritten = lastActivity is { } last && now - last <= _options.WorkingWindow;

        if (subagent)
        {
            // A sub-agent's prompts, tool calls, model and context window belong to the sub-agent, not the parent chat.
            // Its token usage counts towards the parent session, and its writes are the parent's activity: the parent is
            // waiting on the Task call that runs it. A quiet sub-agent says nothing about whether the parent is idle.
            return new TranscriptUpdate
            {
                SessionId = sessionId,
                TranscriptPath = path,
                ObservedAt = now,
                LastActivityAt = lastActivity,
                Usage = usage,
                MessageIds = newMessageIds,
                InferredSignal = recentlyWritten ? SessionSignal.ToolUse : null,
                Historical = historical,
            };
        }

        // Spec: a write within the working window means Working; otherwise a trailing assistant tool call
        // without its result means Waiting (best effort, e.g. a permission prompt); otherwise Idle.
        // A pass cut short by the byte cap ends mid-file, and a pass of lines without a timestamp (Claude Code's metadata
        // lines) says nothing about activity: neither moves the state.
        var inferred = partial || lastActivity is null ? (SessionSignal?)null
            : recentlyWritten ? SessionSignal.ToolUse
            : pendingToolUse ? SessionSignal.Notification
            : SessionSignal.Stop;

        return new TranscriptUpdate
        {
            SessionId = sessionId,
            TranscriptPath = path,
            ObservedAt = now,
            Title = title,
            Cwd = cwd,
            Model = model,
            LastActivityAt = lastActivity,
            Usage = usage,
            MessageIds = newMessageIds,
            LatestContext = latestContext,
            InferredSignal = inferred,
            PendingToolUse = partial ? null : pendingToolUse,
            Interrupted = !partial && interrupted,
            Historical = historical,
        };
    }

    /// <summary>Watches the transcript folder; the loop calls this at start and on refresh. Internal so a test can have a watching indexer.</summary>
    internal void StartWatcher()
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
            _watcher.Error += (sender, e) =>
            {
                if (!ReferenceEquals(sender, _watcher))
                {
                    return; // a watcher already replaced by a fresh one
                }

                var error = e.GetException();
                if (error is InternalBufferOverflowException)
                {
                    _logger.LogWarning(error, "Transcript watcher missed events; scanning everything");
                    _refreshAll = true;
                }
                else
                {
                    // After any other error, such as its folder being deleted, a watcher may raise nothing more, even while
                    // EnableRaisingEvents still says true.
                    _watching = false;
                    _logger.LogWarning(error, "Transcript watcher error; falling back to periodic scans");
                }

                _dirty = true;
            };
            _watching = true;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "Cannot watch {Dir}; using periodic scans only", _claude.ProjectsDirectory);
            _watching = false;
            _watcher = null;
        }
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        _scanGate.Dispose();
        base.Dispose();
    }

    /// <summary>A file a pass could not read: how often it failed and when it is tried again.</summary>
    private sealed record Retry(string Path, int Attempts, DateTimeOffset DueAt);

    private sealed class FileState
    {
        public long Offset { get; set; }
        public DateTimeOffset LastWriteUtc { get; set; }
        public string? SessionId { get; set; }

        /// <summary>The chat's title as far as the file is read, and where it came from; both travel with the cursor.</summary>
        public string? Title { get; set; }
        public TitleSource TitleSource { get; set; }
        public bool PendingToolUse { get; set; }

        /// <summary>An interrupt that ended a pass cut short by the byte cap, reported with the pass that reaches the end.</summary>
        public bool CarriedInterrupt { get; set; }

        /// <summary>
        /// Reading the file again after a rewrite: the /rename names up to the current one were seen and are not offered
        /// again, whatever pass of the re-read they fall into; from the current one on, a different name is a new rename.
        /// </summary>
        public bool SkipCustomUntilCurrent { get; set; }

        /// <summary>The title found while the transcript was historical, until a live update carries it.</summary>
        public string? UndeliveredTitle { get; set; }

        /// <summary>
        /// The newest timestamp of usage counted from this file. After a restart, until the next count, the last write indexed
        /// before it. Claude Code stamps an assistant message when it starts, so assistant lines are in stamp order.
        /// </summary>
        public DateTimeOffset? LastCountedAt { get; set; }

        /// <summary>After a rewrite: usage stamped at or before this was counted before it.</summary>
        public DateTimeOffset? CountedUntil { get; set; }

        /// <summary>
        /// The file was rewritten: read it again from the start. The usage it held is not counted again, even when the message
        /// id memory no longer holds its ids (it keeps only the newest). Its title was already found and is not sent again:
        /// the lines read again offer nothing above its source, and its /rename names nothing up to the current one.
        /// </summary>
        public void Reset()
        {
            Offset = 0;
            PendingToolUse = false;
            CarriedInterrupt = false;
            CountedUntil = LastCountedAt;
            SkipCustomUntilCurrent = TitleSource == TitleSource.Custom;
        }
    }

    /// <summary>Bounded, insertion-ordered set of assistant message ids across every transcript file.</summary>
    private sealed class MessageMemory(int capacity)
    {
        private readonly Queue<string> _order = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        /// <returns>True when the id was not seen before.</returns>
        public bool Remember(string id)
        {
            if (!_seen.Add(id))
            {
                return false;
            }

            _order.Enqueue(id);
            while (_order.Count > capacity)
            {
                _seen.Remove(_order.Dequeue());
            }

            return true;
        }
    }
}
