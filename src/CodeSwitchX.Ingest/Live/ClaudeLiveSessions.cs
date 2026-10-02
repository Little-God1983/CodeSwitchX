using System.ComponentModel;
using System.Text.Json;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Ingest.Live;

/// <summary>
/// The chats open in a VS Code tab right now, from the records Claude Code keeps of its own processes: each writes
/// <c>sessions\&lt;pid&gt;.json</c> with its session id, where it runs and the name other Claude sessions message it by
/// (<c>SendMessage</c>). A chat in a VS Code tab has one from the moment the tab opens until it closes. A record stays
/// behind when its process is killed, and Windows reuses process ids, so one counts only while the very process that wrote
/// it runs: the one with its id that started when the record says. The format is Claude Code's own and not documented
/// (seen with 2.1.286 and 2.1.287): whatever cannot be read as expected is skipped, and then names nothing; a record
/// that lacks what this needs is logged once, as that is what a new format looks like.
/// </summary>
/// <remarks>
/// Not the engine's <see cref="SessionSnapshot.ClaudePid"/>: the engine learns it from a chat's hooks, and a tab opened
/// again may have run none since (seen: a tab reopened while the app ran, open and idle, that the Yard showed Stale
/// without one). The engine also keeps a process it may not open as alive, which is right for not flagging a chat Errored and
/// wrong for naming one to send to (a chat of the user's can always be opened).
/// </remarks>
public sealed class ClaudeLiveSessions
{
    /// <summary>How long one read of the folder serves: the Yard's tools ask for every chat at once.</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How far the process's start may be from the record's: how Claude Code takes it is not documented, so its precision
    /// may differ. A process that took the id since started after the chat's ended, which is far later.
    /// </summary>
    internal static readonly long StartTolerance = TimeSpan.TicksPerSecond;

    /// <summary>A record is a few hundred bytes; anything far bigger is not one.</summary>
    private const long MaxRecordBytes = 256 * 1024;

    /// <summary>What the records of a chat in a VS Code tab say; one run in a terminal or by <c>claude -p</c> says otherwise.</summary>
    private const string VsCodeEntrypoint = "claude-vscode";

    private const string InteractiveKind = "interactive";

    private readonly string _directory;
    private readonly Func<int, long?> _startOf;
    private readonly TimeProvider _time;
    private readonly ILogger<ClaudeLiveSessions> _logger;
    private readonly Lock _gate = new();
    private IReadOnlyDictionary<string, List<Record>> _records = new Dictionary<string, List<Record>>();
    private readonly Dictionary<string, string?> _names = new(StringComparer.OrdinalIgnoreCase);
    private long? _readAt;
    private bool _formatLogged;

    public ClaudeLiveSessions(ClaudeCodePaths paths, TimeProvider time, ILogger<ClaudeLiveSessions> logger)
        : this(paths, StartOf, time, logger)
    {
    }

    /// <param name="startOf">When the process with an id started, as a UTC file time; null when none runs or it may not be opened.</param>
    internal ClaudeLiveSessions(ClaudeCodePaths paths, Func<int, long?> startOf, TimeProvider time, ILogger<ClaudeLiveSessions> logger)
    {
        _directory = Path.Combine(paths.ClaudeDirectory, "sessions");
        _startOf = startOf;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// The name the chat open in a VS Code tab is messaged by; null when it is not open in one (closed, or run in a terminal
    /// or by <c>claude -p</c>). Any thread; never throws.
    /// </summary>
    public string? NameOf(string sessionId)
    {
        // Read under the lock: a caller that comes meanwhile wants the same fresh read, and the map it would get instead
        // is a second old (empty, just after the start). Only the records of a chat asked for have their process looked up.
        lock (_gate)
        {
            if (_readAt is not { } readAt || _time.GetElapsedTime(readAt) >= MaxAge)
            {
                _records = Read();
                _names.Clear();
                _readAt = _time.GetTimestamp();
            }

            if (!_names.TryGetValue(sessionId, out var name))
            {
                name = _records.GetValueOrDefault(sessionId)?.FirstOrDefault(Runs)?.Name;
                _names[sessionId] = name;
            }

            return name;
        }
    }

    /// <summary>
    /// Every chat open in a VS Code tab right now, read afresh: a chat whose tab opened a moment ago is there. Any
    /// thread; never throws.
    /// </summary>
    public IReadOnlyList<LiveChat> RunningNow()
    {
        lock (_gate)
        {
            _records = Read();
            _names.Clear();
            _readAt = _time.GetTimestamp();
            return _records.Values.SelectMany(r => r).Where(Runs).Select(r => new LiveChat(r.Pid, r.SessionId, r.Name)).ToList();
        }
    }

    private bool Runs(Record record) => _startOf(record.Pid) is { } start && Math.Abs(start - record.ProcessStart) < StartTolerance;

    /// <summary>The records of the chats in VS Code tabs by session, the one updated last first.</summary>
    private Dictionary<string, List<Record>> Read()
    {
        var records = new Dictionary<string, List<Record>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(_directory))
            {
                return records;
            }

            // The key files next to the records (<pid>.<hash>.key) are secrets of the chats, and never opened.
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var pid) || ReadRecord(file) is not { } record || record.Pid != pid)
                {
                    continue;
                }

                if (!records.TryGetValue(record.SessionId, out var ofChat))
                {
                    records[record.SessionId] = ofChat = [];
                }

                ofChat.Add(record);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder went, or may not be listed: what was read so far stands.
        }

        foreach (var known in records.Values)
        {
            known.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        }

        return records;
    }

    /// <summary>The record of a chat in a VS Code tab; null for anything else, or one that cannot be read.</summary>
    private Record? ReadRecord(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxRecordBytes)
            {
                return null;
            }

            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out var id)
                || Text(root, "sessionId") is not { Length: > 0 } sessionId
                || Text(root, "name") is not { Length: > 0 } name)
            {
                return null;
            }

            var entrypoint = Text(root, "entrypoint");
            var kind = Text(root, "kind");
            var processStart = FileTime(root, "procStart");
            if (entrypoint is null || kind is null || processStart is null)
            {
                LogFormatOnce(file);
                return null;
            }

            if (entrypoint != VsCodeEntrypoint || kind != InteractiveKind)
            {
                return null;
            }

            var updatedAt = root.TryGetProperty("updatedAt", out var at) && at.TryGetInt64(out var ms) ? ms : 0;
            return new Record(id, sessionId, name, updatedAt, processStart.Value);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null; // being rewritten, or just deleted: the next read has it
        }
    }

    private void LogFormatOnce(string file)
    {
        if (_formatLogged)
        {
            return;
        }

        _formatLogged = true;
        _logger.LogWarning(
            "Claude Code's record {File} has no entrypoint, kind or procStart: its format changed, and Raven cannot tell the chats in VS Code anything until this app reads it",
            file);
    }

    /// <summary>The process's start; one that may not be opened is no chat of the user's.</summary>
    private static long? StartOf(int pid)
    {
        try
        {
            return SystemProcessProbe.StartOf(pid);
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A file time, written as a string (it does not fit a JavaScript number) or as a number.</summary>
    private static long? FileTime(JsonElement element, string property) =>
        !element.TryGetProperty(property, out var value) ? null
        : value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var text) ? text
        : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number
        : null;

    /// <param name="ProcessStart">When the process that wrote it started, as a UTC file time.</param>
    private sealed record Record(int Pid, string SessionId, string Name, long UpdatedAt, long ProcessStart);
}

/// <summary>A chat open in a VS Code tab.</summary>
/// <param name="Pid">Its claude.exe.</param>
/// <param name="Name">The name it is messaged by (<c>SendMessage</c>).</param>
public sealed record LiveChat(int Pid, string SessionId, string Name);
