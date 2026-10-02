using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeSwitchX.Core.Paths;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Hosting.VsCode.Companion;

/// <summary>The VS Code windows the companion runs in, and a way to ask one of them to do something.</summary>
public interface ICompanionWindows
{
    /// <summary>The window that shows the workspace, with the companion running in it; null for none. Any thread; never throws.</summary>
    CompanionWindow? Find(Workspace workspace);

    /// <summary>Sends one command to the window's companion and returns its answer; an answer that says why not when it cannot be reached.</summary>
    Task<CompanionAnswer> SendAsync(CompanionWindow window, string command, CancellationToken ct);
}

/// <summary>A VS Code window as its companion describes it.</summary>
/// <param name="Pid">The window's extension host: the parent of every claude.exe Claude Code's extension starts in the window.</param>
/// <param name="Folders">The window's folders, in the order VS Code has them; a new Claude Code chat runs in the first.</param>
public sealed record CompanionWindow(int Pid, string Pipe, string Token, IReadOnlyList<string> Folders, string? WorkspaceFile, string? Version);

/// <param name="Pid">The extension host's process id, on success.</param>
/// <param name="Error">Why not, in words for the user; null on success.</param>
public sealed record CompanionAnswer(bool Ok, int? Pid = null, string? Version = null, string? Error = null);

/// <summary>
/// Reads the records the companion extension (<c>vscode-companion/</c>) writes: each window's companion listens on a named
/// pipe of its own and writes <c>companion\&lt;extension host pid&gt;.json</c> with the pipe, a token, its folders and its
/// workspace file. A record stays behind when VS Code is killed, and Windows reuses process ids, so one counts only while a
/// process with its id runs that started before the record was written.
/// </summary>
public sealed class CompanionWindows : ICompanionWindows
{
    /// <summary>How long a request may take: opening a chat tab is quick, a window that hangs is not waited for.</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A record is a few hundred bytes; anything far bigger is not one.</summary>
    private const long MaxRecordBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly string _directory;
    private readonly IProcessProbe _probe;

    public CompanionWindows(string directory, IProcessProbe probe)
    {
        _directory = directory;
        _probe = probe;
    }

    /// <summary>Where the companion writes its records: <c>%LOCALAPPDATA%\CodeSwitchX\companion</c>, as its extension.js has it.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeSwitchX", "companion");

    /// <summary>
    /// A workspace with a .code-workspace file is the window that has that file open; a folder is the window whose only
    /// folder it is. Of two such windows (a record left by a window whose host restarted), the newer record wins.
    /// </summary>
    public CompanionWindow? Find(Workspace workspace)
    {
        try
        {
            var wanted = PathNormalizer.Normalize(workspace.Target);
            var byFile = workspace.WorkspaceFile is { Length: > 0 };
            return Records()
                .Where(r => byFile
                    ? r.Window.WorkspaceFile is { } file && Same(file, wanted)
                    : r.Window.WorkspaceFile is null && r.Window.Folders is [var only] && Same(only, wanted))
                .OrderByDescending(r => r.Written)
                .Select(r => r.Window)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>How long a request may take here; <see cref="RequestTimeout"/> but in tests.</summary>
    internal TimeSpan Timeout { get; init; } = RequestTimeout;

    public async Task<CompanionAnswer> SendAsync(CompanionWindow window, string command, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var name = window.Pipe.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase) ? window.Pipe[@"\\.\pipe\".Length..] : window.Pipe;
            await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var request = JsonSerializer.Serialize(new { token = window.Token, command }) + "\n";
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(request), timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);

            using var reader = new StreamReader(pipe, Encoding.UTF8);
            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            return line is null
                ? new CompanionAnswer(false, Error: "The VS Code window closed the connection without an answer.")
                : JsonSerializer.Deserialize<Answer>(line, Json) is { } answer
                    ? new CompanionAnswer(answer.Ok, answer.Pid, answer.Version, answer.Ok ? null : answer.Error ?? "The VS Code window said no, without a reason.")
                    : new CompanionAnswer(false, Error: "The VS Code window gave no answer that could be read.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CompanionAnswer(false, Error: $"The VS Code window did not answer within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or TimeoutException)
        {
            return new CompanionAnswer(false, Error: $"The VS Code window could not be reached: {ex.Message}");
        }
    }

    private IEnumerable<(CompanionWindow Window, DateTime Written)> Records()
    {
        if (!Directory.Exists(_directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var pid) || Read(file) is not { } window || window.Pid != pid)
            {
                continue;
            }

            var written = File.GetLastWriteTimeUtc(file);
            if (_probe.IsAlive(pid, new DateTimeOffset(written, TimeSpan.Zero)))
            {
                yield return (window, written);
            }
        }
    }

    private static CompanionWindow? Read(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxRecordBytes)
            {
                return null;
            }

            var record = JsonSerializer.Deserialize<Record>(stream, Json);
            return record is { Pid: > 0, Pipe.Length: > 0, Token.Length: > 0 }
                ? new CompanionWindow(record.Pid, record.Pipe, record.Token, record.Folders?.Where(f => !string.IsNullOrWhiteSpace(f)).ToList() ?? [],
                    string.IsNullOrWhiteSpace(record.WorkspaceFile) ? null : record.WorkspaceFile, record.Version)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null; // being written, or just deleted: the next look has it
        }
    }

    private static bool Same(string path, string normalized)
    {
        try
        {
            return PathNormalizer.Normalize(path) == normalized;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private sealed record Record(
        [property: JsonPropertyName("pid")] int Pid,
        [property: JsonPropertyName("pipe")] string? Pipe,
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("folders")] List<string>? Folders,
        [property: JsonPropertyName("workspaceFile")] string? WorkspaceFile,
        [property: JsonPropertyName("version")] string? Version);

    private sealed record Answer(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("pid")] int? Pid,
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("error")] string? Error);
}
