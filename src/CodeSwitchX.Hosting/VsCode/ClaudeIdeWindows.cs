using System.ComponentModel;
using System.Text.Json;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.Hosting.VsCode;

/// <summary>
/// Tells the VS Code window of a chat from the lock files Claude Code's VS Code extension writes: each window's
/// extension host listens on a local port and writes <c>ide\&lt;port&gt;.lock</c> with the window's folders. A chat
/// the extension starts runs its claude under that extension host, so the window is the one whose port a process
/// above the claude listens on. The lock's own <c>pid</c> is VS Code's main process, which every window shares.
/// Old lock files stay behind when a window closes; a port nobody above the claude listens on is no match.
/// </summary>
public sealed class ClaudeIdeWindows : IIdeWindows
{
    /// <summary>The extension host is the claude's parent; a claude started by another chat's claude sits a few levels lower.</summary>
    internal const int MaxDepth = 8;

    private readonly string _lockDirectory;
    private readonly Func<IReadOnlyDictionary<int, int>> _listeners;
    private readonly Func<int, int, IReadOnlyList<int>> _ancestors;

    public ClaudeIdeWindows(ClaudeCodePaths paths)
        : this(Path.Combine(paths.ClaudeDirectory, "ide"), ProcessTable.LoopbackListeners, ProcessTable.Ancestors)
    {
    }

    /// <param name="listeners">The process listening on each local TCP port.</param>
    /// <param name="ancestors">The processes above a process, nearest first, at most as many as asked.</param>
    internal ClaudeIdeWindows(string lockDirectory, Func<IReadOnlyDictionary<int, int>> listeners, Func<int, int, IReadOnlyList<int>> ancestors)
    {
        _lockDirectory = lockDirectory;
        _listeners = listeners;
        _ancestors = ancestors;
    }

    public IReadOnlyList<string>? FoldersOf(int claudePid)
    {
        try
        {
            var ancestors = _ancestors(claudePid, MaxDepth);
            if (ancestors.Count == 0 || !Directory.Exists(_lockDirectory))
            {
                return null;
            }

            var owners = new Dictionary<int, string>();
            var listeners = _listeners();
            foreach (var file in Directory.EnumerateFiles(_lockDirectory, "*.lock"))
            {
                if (int.TryParse(Path.GetFileNameWithoutExtension(file), out var port) && listeners.TryGetValue(port, out var owner))
                {
                    owners.TryAdd(owner, file);
                }
            }

            foreach (var pid in ancestors)
            {
                if (owners.TryGetValue(pid, out var file))
                {
                    return FoldersIn(file);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The lock's <c>workspaceFolders</c>; null when it cannot be read or names none. Nothing else of the file is read.</summary>
    private static IReadOnlyList<string>? FoldersIn(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var lockFile = JsonDocument.Parse(stream);
            if (lockFile.RootElement.ValueKind != JsonValueKind.Object
                || !lockFile.RootElement.TryGetProperty("workspaceFolders", out var folders) || folders.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var paths = folders.EnumerateArray()
                .Where(f => f.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(f.GetString()))
                .Select(f => f.GetString()!)
                .ToList();
            return paths.Count > 0 ? paths : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
