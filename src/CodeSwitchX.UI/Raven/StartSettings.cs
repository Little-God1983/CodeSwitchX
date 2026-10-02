using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSwitchX.Core.Yard;

namespace CodeSwitchX.UI.Raven;

/// <summary>
/// The model and effort of a chat VS Code is about to start, set in the folder's <c>.claude\settings.local.json</c> for
/// that moment: VS Code's command that opens a chat takes neither, and a new chat reads that file as it starts (before it
/// records itself in <c>~/.claude/sessions</c>). A chat keeps both once it runs, so the file is put back right after:
/// byte for byte when nobody wrote it meanwhile (deleted when there was none), else only the two keys set here are
/// undone, and only while they still hold what was set, so what Claude Code or the user wrote meanwhile stays. What is
/// needed to put it back is saved first in <c>pendingDirectory</c>, so a CodeSwitchX that dies in between puts it back at
/// its next start (<see cref="RecoverAll"/>), and a put-back that failed is done by the next start in the folder before it
/// reads the file; when that fails too, the start is refused, as the file would pass Raven's model off as the user's. A
/// chat the user opens by hand in the same folder in that moment gets them too.
/// </summary>
public sealed class StartSettings : IDisposable
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The notes and the files they put back are touched by one start, put-back or recovery at a time.</summary>
    private static readonly Lock Gate = new();

    private readonly Pending _pending;
    private readonly string _pendingFile;
    private bool _disposed;

    private StartSettings(Pending pending, string pendingFile)
    {
        _pending = pending;
        _pendingFile = pendingFile;
    }

    /// <summary>Whether the file is free of what was set here again; false only when it could not be read or written.</summary>
    public bool Restored { get; private set; }

    /// <summary>The settings file Claude Code reads in a folder for this user only.</summary>
    public static string FileIn(string folder) => Path.Combine(folder, ".claude", "settings.local.json");

    /// <summary>Sets them; null when neither is given, and nothing is written.</summary>
    /// <param name="pendingDirectory">Where what puts the file back is kept until it is put back.</param>
    /// <exception cref="YardActionException">The file is there but no JSON object, or cannot be written.</exception>
    public static StartSettings? Apply(string folder, string? model, string? effort, string pendingDirectory)
    {
        if (model is null && effort is null)
        {
            return null;
        }

        var file = FileIn(folder);
        var directory = Path.GetDirectoryName(file)!;
        var pendingFile = PendingFileOf(pendingDirectory, file);
        lock (Gate)
        {
            try
            {
                return ApplyLocked(file, directory, pendingFile, model, effort);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new YardActionException($"The model and effort could not be set for the chat: {file} could not be written ({ex.Message}).");
            }
        }
    }

    private static StartSettings ApplyLocked(string file, string directory, string pendingFile, string? model, string? effort)
    {
        // An earlier start that could not put the file back left its note: read now, the file would pass Raven's model off
        // as the user's, and that note, the one that knows what the file really was, would be written over.
        if (File.Exists(pendingFile))
        {
            if (ReadPending(pendingFile) is { } earlier && !PutBack(earlier))
            {
                throw new YardActionException($"{file} still holds the model of an earlier chat Raven started, and it could not be put back. "
                    + "Start the chat without a model and effort, or check the file.");
            }

            File.Delete(pendingFile);
        }

        var original = File.Exists(file) ? File.ReadAllBytes(file) : null;
        var settings = ParseOrNull(original) ?? throw new YardActionException(
            $"{file} is not valid JSON, so the model and effort cannot be set for the chat. Fix the file, or start the chat without them.");

        var set = new Dictionary<string, string>();
        if (model is not null)
        {
            set["model"] = model;
        }

        if (effort is not null)
        {
            set["effortLevel"] = effort;
        }

        var before = set.Keys.ToDictionary(key => key, key => settings[key]?.ToJsonString());
        foreach (var (key, value) in set)
        {
            settings[key] = value;
        }

        var written = JsonSerializer.SerializeToUtf8Bytes(settings, Indented);
        var pending = new Pending(file, original is null ? null : Convert.ToBase64String(original), !Directory.Exists(directory),
            Convert.ToBase64String(written), set, before);

        // Saved before the file is touched: from here on, a crash leaves what puts it back.
        Directory.CreateDirectory(Path.GetDirectoryName(pendingFile)!);
        File.WriteAllText(pendingFile, JsonSerializer.Serialize(pending));

        Directory.CreateDirectory(directory);
        File.WriteAllBytes(file, written);
        return new StartSettings(pending, pendingFile);
    }

    /// <summary>One note per settings file, named after its path.</summary>
    private static string PendingFileOf(string pendingDirectory, string file) =>
        Path.Combine(pendingDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.ToUpperInvariant())))[..16] + ".json");

    /// <summary>The note; null when it cannot be read as one (cut short, or not ours): nothing it says can be trusted.</summary>
    private static Pending? ReadPending(string pendingFile)
    {
        try
        {
            return JsonSerializer.Deserialize<Pending>(File.ReadAllText(pendingFile));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Puts the file back; never throws.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (Gate)
        {
            // Kept when it fails: the next start in the folder, or of CodeSwitchX, puts the file back from it.
            Restored = PutBack(_pending);
            if (Restored)
            {
                TryDelete(_pendingFile);
            }
        }
    }

    /// <summary>
    /// Puts back every file a start of an earlier run left set, as a CodeSwitchX that ended in the middle of one did. Notes
    /// written since <paramref name="runStarted"/> belong to starts of this run, which put their files back themselves.
    /// Never throws.
    /// </summary>
    /// <returns>The settings files it could not put back.</returns>
    public static IReadOnlyList<string> RecoverAll(string pendingDirectory, DateTime runStarted)
    {
        var failed = new List<string>();
        try
        {
            if (!Directory.Exists(pendingDirectory))
            {
                return failed;
            }

            foreach (var pendingFile in Directory.GetFiles(pendingDirectory, "*.json"))
            {
                lock (Gate)
                {
                    if (!File.Exists(pendingFile) || File.GetLastWriteTimeUtc(pendingFile) >= runStarted)
                    {
                        continue;
                    }

                    if (ReadPending(pendingFile) is not { } pending || PutBack(pending))
                    {
                        TryDelete(pendingFile);
                    }
                    else
                    {
                        failed.Add(pending.File);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // what was not looked at is looked at next start
        }

        return failed;
    }

    /// <summary>
    /// Byte for byte when the file is as it was written here; else only the keys set here that still hold what was set
    /// go back to what they were. A file that is gone, or no JSON now, is the user's: nothing is done to it. True once
    /// nothing set here is left in it.
    /// </summary>
    private static bool PutBack(Pending pending)
    {
        try
        {
            if (!File.Exists(pending.File))
            {
                return true;
            }

            var current = File.ReadAllBytes(pending.File);
            if (current.AsSpan().SequenceEqual(Convert.FromBase64String(pending.Written)))
            {
                if (pending.Original is { } original)
                {
                    File.WriteAllBytes(pending.File, Convert.FromBase64String(original));
                }
                else
                {
                    File.Delete(pending.File);
                    var directory = Path.GetDirectoryName(pending.File)!;
                    if (pending.CreatedDirectory && !Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory);
                    }
                }

                return true;
            }

            if (ParseOrNull(current) is not { } settings)
            {
                return true;
            }

            var changed = false;
            foreach (var (key, value) in pending.Set)
            {
                if (settings[key] is JsonValue held && held.TryGetValue<string>(out var text) && text == value)
                {
                    if (pending.Before.GetValueOrDefault(key) is { } before)
                    {
                        settings[key] = JsonNode.Parse(before);
                    }
                    else
                    {
                        settings.Remove(key);
                    }

                    changed = true;
                }
            }

            if (changed)
            {
                File.WriteAllBytes(pending.File, JsonSerializer.SerializeToUtf8Bytes(settings, Indented));
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or JsonException)
        {
            return false;
        }
    }

    /// <summary>The file's JSON object, read past a byte order mark (Notepad and PowerShell 5 write one); empty for none; null when it is no JSON object.</summary>
    private static JsonObject? ParseOrNull(byte[]? bytes)
    {
        var json = bytes.AsSpan();
        json = json.StartsWith(Utf8Bom) ? json[Utf8Bom.Length..] : json;
        try
        {
            return json.IsEmpty ? [] : JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // put back already; the next start finds the file as it should be and deletes this
        }
    }

    /// <param name="Original">The file's bytes before, base64; null when there was none.</param>
    /// <param name="Written">What was written here, base64.</param>
    /// <param name="Set">The keys set here, with what they were set to.</param>
    /// <param name="Before">Each key's JSON before; null when it was not there.</param>
    private sealed record Pending(string File, string? Original, bool CreatedDirectory, string Written, Dictionary<string, string> Set,
        Dictionary<string, string?> Before);
}
