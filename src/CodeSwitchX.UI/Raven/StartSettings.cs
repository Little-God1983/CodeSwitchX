using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSwitchX.Core.Yard;

namespace CodeSwitchX.UI.Raven;

/// <summary>
/// The model and effort of a chat VS Code is about to start, set in the folder's <c>.claude\settings.local.json</c> for
/// that moment: VS Code's command that opens a chat takes neither, and a new chat reads that file as it starts. A chat
/// keeps both once it runs, so the file is put back right after, exactly as it was (deleted when there was none). Every
/// other key in it stays. A chat the user opens by hand in the same folder in that moment gets them too.
/// </summary>
public sealed class StartSettings : IDisposable
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly string _file;
    private readonly byte[]? _original;
    private readonly bool _createdDirectory;
    private readonly byte[] _written;
    private bool _disposed;

    private StartSettings(string file, byte[]? original, bool createdDirectory, byte[] written)
    {
        _file = file;
        _original = original;
        _createdDirectory = createdDirectory;
        _written = written;
    }

    /// <summary>Whether the file is as it was before; false once the user changed it meanwhile, which is then left as they made it.</summary>
    public bool Restored { get; private set; }

    /// <summary>The settings file Claude Code reads in a folder for this user only.</summary>
    public static string FileIn(string folder) => Path.Combine(folder, ".claude", "settings.local.json");

    /// <summary>Sets them; null when neither is given, and nothing is written.</summary>
    /// <exception cref="YardActionException">The file is there but no JSON object, or cannot be written.</exception>
    public static StartSettings? Apply(string folder, string? model, string? effort)
    {
        if (model is null && effort is null)
        {
            return null;
        }

        var file = FileIn(folder);
        var directory = Path.GetDirectoryName(file)!;
        try
        {
            var original = File.Exists(file) ? File.ReadAllBytes(file) : null;
            JsonObject settings;
            try
            {
                // Notepad and PowerShell 5 write a byte order mark, which Claude Code reads past and the JSON reader does not.
                var json = original.AsSpan();
                json = json.StartsWith(Utf8Bom) ? json[Utf8Bom.Length..] : json;
                settings = json.IsEmpty ? [] : JsonNode.Parse(json)?.AsObject() ?? [];
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                throw new YardActionException($"{file} is not valid JSON, so the model and effort cannot be set for the chat. Fix the file, or start the chat without them.");
            }

            if (model is not null)
            {
                settings["model"] = model;
            }

            if (effort is not null)
            {
                settings["effortLevel"] = effort;
            }

            var createdDirectory = !Directory.Exists(directory);
            Directory.CreateDirectory(directory);
            var written = JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllBytes(file, written);
            return new StartSettings(file, original, createdDirectory, written);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new YardActionException($"The model and effort could not be set for the chat: {file} could not be written ({ex.Message}).");
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
        try
        {
            if (!File.Exists(_file) || !File.ReadAllBytes(_file).AsSpan().SequenceEqual(_written))
            {
                return; // the user changed or removed it meanwhile: theirs stands
            }

            if (_original is null)
            {
                File.Delete(_file);
                var directory = Path.GetDirectoryName(_file)!;
                if (_createdDirectory && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            else
            {
                File.WriteAllBytes(_file, _original);
            }

            Restored = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Restored stays false, and the caller says so.
        }
    }
}
