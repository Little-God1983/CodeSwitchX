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
    private readonly Func<IReadOnlyList<(int Port, int Pid)>> _listeners;
    private readonly Func<IReadOnlyDictionary<int, (int Parent, string Name)>> _processes;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private Tables? _tables;

    public ClaudeIdeWindows(ClaudeCodePaths paths)
        : this(Path.Combine(paths.ClaudeDirectory, "ide"), ProcessTable.Listeners, ProcessTable.Processes, TimeProvider.System)
    {
    }

    /// <param name="listeners">Each local TCP port with a process listening on it; a port can appear once per process.</param>
    /// <param name="processes">Every process with its parent and name.</param>
    internal ClaudeIdeWindows(string lockDirectory, Func<IReadOnlyList<(int Port, int Pid)>> listeners,
        Func<IReadOnlyDictionary<int, (int Parent, string Name)>> processes, TimeProvider time)
    {
        _lockDirectory = lockDirectory;
        _listeners = listeners;
        _processes = processes;
        _time = time;
    }

    /// <summary>
    /// The processes a hook event names above the claude are tried first; when none of them holds a lock (the hook's
    /// chain stops at its depth, a claude started by another chat's sits deep), the claude is walked up in the process
    /// table. A lock that cannot be read as it is written, or a claude not yet in a process table read before it started,
    /// leaves nothing known (false): the engine asks again.
    /// </summary>
    public bool TryFoldersOf(int claudePid, IReadOnlyList<int>? ancestors, out IReadOnlyList<string>? folders)
    {
        folders = null;
        try
        {
            lock (_gate)
            {
                var tables = Fresh();
                if (tables.LockOf.Count == 0)
                {
                    return true;
                }

                var file = ancestors?.Take(MaxDepth).Select(tables.LockOf.GetValueOrDefault).FirstOrDefault(f => f is not null);
                if (file is null)
                {
                    var above = tables.AncestorsOf(claudePid, _processes, out var readNow);
                    if (above is null && !readNow)
                    {
                        _tables = tables = Read(); // the process table was older than the claude: read it again
                        above = tables.AncestorsOf(claudePid, _processes, out _);
                    }

                    above ??= []; // the claude is gone

                    file = above.Select(tables.LockOf.GetValueOrDefault).FirstOrDefault(f => f is not null);
                }

                return file is null || TryFoldersIn(file, out folders);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return false;
        }
    }

    private Tables Fresh() =>
        _tables is { } tables && _time.GetElapsedTime(tables.ReadAt) < MaxAge ? tables : _tables = Read();

    /// <summary>
    /// The lock folder and who listens where. A process that listens on the ports of several locks (an old lock whose
    /// port number its other server took) gets the newest of them: the extension rewrites its window's lock as it starts
    /// and as the window's folders change.
    /// </summary>
    private Tables Read()
    {
        var lockOf = new Dictionary<int, string>();
        if (Directory.Exists(_lockDirectory))
        {
            var owners = _listeners().ToLookup(l => l.Port, l => l.Pid);
            var locks = Directory.EnumerateFiles(_lockDirectory, "*.lock")
                .Select(file => (File: file, Port: int.TryParse(Path.GetFileNameWithoutExtension(file), out var port) ? port : -1))
                .Where(l => owners.Contains(l.Port))
                .OrderByDescending(l => File.GetLastWriteTimeUtc(l.File));
            foreach (var (file, port) in locks)
            {
                foreach (var owner in owners[port])
                {
                    lockOf.TryAdd(owner, file);
                }
            }
        }

        return new Tables(_time.GetTimestamp(), lockOf);
    }

    /// <summary>
    /// The lock's <c>workspaceFolders</c>: null (and true) when it names none, false when it cannot be read now (the
    /// extension is rewriting it, the window just closed). Nothing else of the file is read.
    /// </summary>
    private static bool TryFoldersIn(string file, out IReadOnlyList<string>? folders)
    {
        folders = null;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var lockFile = JsonDocument.Parse(stream);
            if (lockFile.RootElement.ValueKind == JsonValueKind.Object
                && lockFile.RootElement.TryGetProperty("workspaceFolders", out var listed) && listed.ValueKind == JsonValueKind.Array)
            {
                var paths = listed.EnumerateArray()
                    .Where(f => f.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(f.GetString()))
                    .Select(f => f.GetString()!)
                    .ToList();
                folders = paths.Count > 0 ? paths : null;
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>One read of the system: the lock file of each process that listens on a lock's port, and, once asked, every process.</summary>
    private sealed class Tables(long readAt, IReadOnlyDictionary<int, string> lockOf)
    {
        private IReadOnlyDictionary<int, (int Parent, string Name)>? _processes;

        public long ReadAt { get; } = readAt;

        public IReadOnlyDictionary<int, string> LockOf { get; } = lockOf;

        /// <summary>
        /// The processes above <paramref name="pid"/>, its parent first, walked as the hook walks them; null when this
        /// read of the process table does not hold it.
        /// </summary>
        /// <param name="readNow">Whether the process table was read for this call, so it cannot be older than the claude.</param>
        public IReadOnlyList<int>? AncestorsOf(int pid, Func<IReadOnlyDictionary<int, (int Parent, string Name)>> read, out bool readNow)
        {
            readNow = _processes is null;
            _processes ??= read();
            return _processes.ContainsKey(pid) ? ProcessChain.Ancestors(pid, _processes, MaxDepth).Select(p => p.Pid).ToList() : null;
        }
    }
}
