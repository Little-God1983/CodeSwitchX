using System.Text.Json;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Ingest.Live;

/// <summary>
/// The chats that run right now, from the records Claude Code keeps of its own processes: each writes
/// <c>sessions\&lt;pid&gt;.json</c> with its session id and the name other Claude sessions message it by
/// (<c>SendMessage</c>). A chat in a VS Code tab has one from the moment the tab opens until it closes. A record stays
/// behind when its process is killed, so one counts only while that process runs. The format is Claude Code's own and
/// not documented (seen with 2.1.287): whatever cannot be read as expected is skipped, and then names nothing.
/// </summary>
public sealed class ClaudeLiveSessions
{
    /// <summary>How long one read of the folder serves: the Yard's tools ask for every chat at once.</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(1);

    /// <summary>A record is a few hundred bytes; anything far bigger is not one.</summary>
    private const long MaxRecordBytes = 256 * 1024;

    private readonly string _directory;
    private readonly IProcessProbe _probe;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private IReadOnlyDictionary<string, string> _names = new Dictionary<string, string>();
    private long? _readAt;

    public ClaudeLiveSessions(ClaudeCodePaths paths, IProcessProbe probe, TimeProvider time)
    {
        _directory = Path.Combine(paths.ClaudeDirectory, "sessions");
        _probe = probe;
        _time = time;
    }

    /// <summary>The name the running chat is messaged by; null when it does not run. Any thread; never throws.</summary>
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

                if (_probe.IsAlive(pid, record.Written))
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
                || Text(root, "name") is not { Length: > 0 } name)
            {
                return null;
            }

            var updatedAt = root.TryGetProperty("updatedAt", out var at) && at.TryGetInt64(out var ms) ? ms : 0;
            return new Record(id, sessionId, name, updatedAt, File.GetLastWriteTimeUtc(file));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null; // being rewritten, or just deleted: the next read has it
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <param name="Written">When the record was last written: its process ran then, so one started later took the id since.</param>
    private sealed record Record(int Pid, string SessionId, string Name, long UpdatedAt, DateTimeOffset Written);
}
