using System.ComponentModel;
using System.Text.Json;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hook;
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

    /// <summary>
    /// How long one read of the system serves: the engine looks up every chat at once on a start, and each read takes
    /// the whole listener table, the lock folder and perhaps every process. A window that opens after a read has its
    /// claude up long after this.
    /// </summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromMilliseconds(250);

    private readonly string _lockDirectory;
    private readonly Func<IReadOnlyDictionary<int, int>> _listeners;
    private readonly Func<IReadOnlyDictionary<int, (int Parent, string Name)>> _processes;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private Tables? _tables;

    public ClaudeIdeWindows(ClaudeCodePaths paths)
        : this(Path.Combine(paths.ClaudeDirectory, "ide"), ProcessTable.Listeners, ProcessTable.Processes, TimeProvider.System)
    {
    }

    /// <param name="listeners">The process listening on each local TCP port.</param>
    /// <param name="processes">Every process with its parent and name.</param>
    internal ClaudeIdeWindows(string lockDirectory, Func<IReadOnlyDictionary<int, int>> listeners, Func<IReadOnlyDictionary<int, (int Parent, string Name)>> processes,
        TimeProvider time)
    {
        _lockDirectory = lockDirectory;
        _listeners = listeners;
        _processes = processes;
        _time = time;
    }

    public bool TryFoldersOf(int claudePid, IReadOnlyList<int>? ancestors, out IReadOnlyList<string>? folders)
    {
        folders = null;
        try
        {
            lock (_gate)
            {
                var tables = Fresh();
                foreach (var pid in (ancestors is { Count: > 0 } ? ancestors : tables.AncestorsOf(claudePid)).Take(MaxDepth))
                {
                    if (tables.LockOf.TryGetValue(pid, out var file))
                    {
                        folders = FoldersIn(file);
                        return true;
                    }
                }

                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return false;
        }
    }

    private Tables Fresh()
    {
        if (_tables is { } tables && _time.GetElapsedTime(tables.ReadAt) < MaxAge)
        {
            return tables;
        }

        var lockOf = new Dictionary<int, string>();
        if (Directory.Exists(_lockDirectory))
        {
            var listeners = _listeners();
            foreach (var file in Directory.EnumerateFiles(_lockDirectory, "*.lock"))
            {
                if (int.TryParse(Path.GetFileNameWithoutExtension(file), out var port) && listeners.TryGetValue(port, out var owner))
                {
                    lockOf.TryAdd(owner, file);
                }
            }
        }

        return _tables = new Tables(_time.GetTimestamp(), lockOf, _processes);
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
        catch (Exception ex) when (ex is JsonException or FileNotFoundException or DirectoryNotFoundException)
        {
            return null; // a window that closed since the folder was listed
        }
    }

    /// <summary>One read of the system: the lock file of each process that listens on a lock's port, and, once asked, every process.</summary>
    private sealed class Tables(long readAt, IReadOnlyDictionary<int, string> lockOf, Func<IReadOnlyDictionary<int, (int Parent, string Name)>> readProcesses)
    {
        private IReadOnlyDictionary<int, (int Parent, string Name)>? _processes;

        public long ReadAt { get; } = readAt;

        public IReadOnlyDictionary<int, string> LockOf { get; } = lockOf;

        /// <summary>The processes above <paramref name="pid"/>, its parent first, walked as the hook walks them; none when it is gone.</summary>
        public IEnumerable<int> AncestorsOf(int pid)
        {
            if (LockOf.Count == 0)
            {
                return []; // no window to find: no need for every process
            }

            _processes ??= readProcesses();
            return ProcessChain.Ancestors(pid, _processes, MaxDepth).Select(p => p.Pid);
        }
    }
}
