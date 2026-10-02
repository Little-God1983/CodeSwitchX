using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CodeSwitchX.Core;

namespace CodeSwitchX.Ingest.Live;

/// <summary>
/// The chats open in a VS Code tab right now, from the records Claude Code keeps of its own processes: each writes
/// <c>sessions\&lt;pid&gt;.json</c> with its session id, where it runs and the name other Claude sessions message it by
/// (<c>SendMessage</c>). A chat in a VS Code tab has one from the moment the tab opens until it closes. A record stays
/// behind when its process is killed, and Windows reuses process ids, so one counts only while the very process that wrote
/// it runs: the one with its id that started when the record says. The format is Claude Code's own and not documented
/// (seen with 2.1.287): whatever cannot be read as expected is skipped, and then names nothing.
/// </summary>
public sealed class ClaudeLiveSessions
{
    /// <summary>How long one read of the folder serves: the Yard's tools ask for every chat at once.</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(1);

    /// <summary>A record is a few hundred bytes; anything far bigger is not one.</summary>
    private const long MaxRecordBytes = 256 * 1024;

    /// <summary>What the records of a chat in a VS Code tab say; one run in a terminal or by <c>claude -p</c> says otherwise.</summary>
    private const string VsCodeEntrypoint = "claude-vscode";

    private const string InteractiveKind = "interactive";

    private readonly string _directory;
    private readonly Func<int, long?> _startOf;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private IReadOnlyDictionary<string, string> _names = new Dictionary<string, string>();
    private long? _readAt;

    public ClaudeLiveSessions(ClaudeCodePaths paths, TimeProvider time)
        : this(paths, StartOf, time)
    {
    }

    /// <param name="startOf">When the process with an id started, as a UTC file time; null when none runs or it may not be opened.</param>
    internal ClaudeLiveSessions(ClaudeCodePaths paths, Func<int, long?> startOf, TimeProvider time)
    {
        _directory = Path.Combine(paths.ClaudeDirectory, "sessions");
        _startOf = startOf;
        _time = time;
    }

    /// <summary>The name the chat open in a VS Code tab is messaged by; null when it is not open there. Any thread; never throws.</summary>
    public string? NameOf(string sessionId)
    {
        lock (_gate)
        {
            if (_readAt is not { } readAt || _time.GetElapsedTime(readAt) >= MaxAge)
            {
                _names = Read();
                _readAt = _time.GetTimestamp();
            }

            return _names.GetValueOrDefault(sessionId);
        }
    }

    /// <summary>Each running chat's name; of several records for one chat the one updated last.</summary>
    private Dictionary<string, string> Read()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var updated = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(_directory))
            {
                return names;
            }

            // The key files next to the records (<pid>.<hash>.key) are secrets of the chats, and never opened.
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var pid) || ReadRecord(file) is not { } record || record.Pid != pid)
                {
                    continue;
                }

                if (updated.TryGetValue(record.SessionId, out var known) && known >= record.UpdatedAt)
                {
                    continue;
                }

                if (_startOf(pid) == record.ProcessStart)
                {
                    names[record.SessionId] = record.Name;
                    updated[record.SessionId] = record.UpdatedAt;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder went, or may not be listed: what was read so far stands.
        }

        return names;
    }

    /// <summary>The record of a chat in a VS Code tab; null for anything else, or one that cannot be read.</summary>
    private static Record? ReadRecord(string file)
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
                || Text(root, "name") is not { Length: > 0 } name
                || Text(root, "entrypoint") != VsCodeEntrypoint
                || Text(root, "kind") != InteractiveKind
                || FileTime(root, "procStart") is not { } processStart)
            {
                return null;
            }

            var updatedAt = root.TryGetProperty("updatedAt", out var at) && at.TryGetInt64(out var ms) ? ms : 0;
            return new Record(id, sessionId, name, updatedAt, processStart);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null; // being rewritten, or just deleted: the next read has it
        }
    }

    /// <summary>The process's start as a UTC file time, which is what a record's <c>procStart</c> holds.</summary>
    private static long? StartOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            // StartTime is local time; ToFileTimeUtc keeps the DST flag FromFileTime set, so the fall-back hour converts right.
            return process.HasExited ? null : process.StartTime.ToFileTimeUtc();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Gone, or a process the app may not open (protected or SYSTEM, after the id was reused): a chat of the user's it is not.
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
