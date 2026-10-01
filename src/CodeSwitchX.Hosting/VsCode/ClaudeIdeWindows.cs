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

    /// <summary>
    /// How much earlier than its port's listener a lock may have been written and still be that listener's: the
    /// extension writes its lock just after it begins to listen, and file and socket times are not taken by one clock.
    /// </summary>
    internal static readonly TimeSpan ClockSlack = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The editors that run Claude Code's VS Code extension. A claude whose parent is one of them was started by the
    /// extension's host; one started from a terminal has a shell for its parent.
    /// </summary>
    private static readonly HashSet<string> Editors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code.exe", "Code - Insiders.exe", "VSCodium.exe", "Cursor.exe", "Windsurf.exe",
    };

    private readonly string _lockDirectory;
    private readonly Func<IReadOnlyList<(int Port, int Pid, DateTime? Since)>> _listeners;
    private readonly Func<IReadOnlyDictionary<int, (int Parent, string Name)>> _processes;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private Tables? _tables;

    public ClaudeIdeWindows(ClaudeCodePaths paths)
        : this(Path.Combine(paths.ClaudeDirectory, "ide"), ProcessTable.Listeners, ProcessTable.Processes, TimeProvider.System)
    {
    }

    /// <param name="listeners">Each local TCP port with a process listening on it, and since when; a port can appear once per process.</param>
    /// <param name="processes">Every process with its parent and name.</param>
    internal ClaudeIdeWindows(string lockDirectory, Func<IReadOnlyList<(int Port, int Pid, DateTime? Since)>> listeners,
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
    /// table. Nothing is known yet (false, and the engine asks again) when a lock cannot be read as it is written, when a
    /// claude is not yet in a process table read before it started, and when no lock matches a claude an editor started:
    /// its window's lock is being rewritten. No lock above a claude a shell started is a final "no window".
    /// </summary>
    public bool TryFoldersOf(int claudePid, IReadOnlyList<int>? ancestors, out IReadOnlyList<string>? folders)
    {
        folders = null;
        try
        {
            lock (_gate)
            {
                var tables = Fresh();
                var file = ancestors?.Take(MaxDepth).Select(tables.LockOf.GetValueOrDefault).FirstOrDefault(f => f is not null);
                if (file is not null)
                {
                    return TryFoldersIn(file, out folders);
                }

                if (!tables.ReadProcessesIfNone(_processes) && !tables.Holds(claudePid))
                {
                    tables.ReadProcesses(_processes); // read before the claude started (or the claude is gone)
                }

                file = tables.AncestorsOf(claudePid).Select(tables.LockOf.GetValueOrDefault).FirstOrDefault(f => f is not null);
                return file is not null ? TryFoldersIn(file, out folders) : !tables.StartedByEditor(claudePid);
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
    /// The lock folder and who listens where. A lock written before its port's listener began to listen is not that
    /// listener's: it is an old lock of a closed or crashed window whose port number another server took since. Of the
    /// locks left to one process the newest counts: the extension rewrites its window's lock as the folders change.
    /// </summary>
    private Tables Read()
    {
        var lockOf = new Dictionary<int, string>();
        if (Directory.Exists(_lockDirectory))
        {
            var listeners = _listeners().ToLookup(l => l.Port);
            var locks = Directory.EnumerateFiles(_lockDirectory, "*.lock")
                .Select(file => (File: file, Port: int.TryParse(Path.GetFileNameWithoutExtension(file), out var port) ? port : -1))
                .Where(l => listeners.Contains(l.Port))
                .Select(l => (l.File, l.Port, Written: File.GetLastWriteTimeUtc(l.File)))
                .OrderByDescending(l => l.Written);
            foreach (var (file, port, written) in locks)
            {
                foreach (var listener in listeners[port])
                {
                    if (listener.Since is not { } since || written >= since - ClockSlack)
                    {
                        lockOf.TryAdd(listener.Pid, file);
                    }
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

    /// <summary>
    /// One read of the system: the lock file of each process that listens on a lock's port, and, once asked, every
    /// process. The process table can be read again on its own, for a claude newer than it.
    /// </summary>
    private sealed class Tables(long readAt, IReadOnlyDictionary<int, string> lockOf)
    {
        private IReadOnlyDictionary<int, (int Parent, string Name)> _processes = new Dictionary<int, (int Parent, string Name)>();
        private bool _processesRead;

        public long ReadAt { get; } = readAt;

        public IReadOnlyDictionary<int, string> LockOf { get; } = lockOf;

        /// <summary>Reads the process table unless this read of the system has one; returns whether it read it now.</summary>
        public bool ReadProcessesIfNone(Func<IReadOnlyDictionary<int, (int Parent, string Name)>> read)
        {
            if (_processesRead)
            {
                return false;
            }

            ReadProcesses(read);
            return true;
        }

        public void ReadProcesses(Func<IReadOnlyDictionary<int, (int Parent, string Name)>> read)
        {
            _processes = read();
            _processesRead = true;
        }

        public bool Holds(int pid) => _processes.ContainsKey(pid);

        /// <summary>The processes above <paramref name="pid"/>, its parent first, walked as the hook walks them; none when it is gone.</summary>
        public IEnumerable<int> AncestorsOf(int pid) => ProcessChain.Ancestors(pid, _processes, MaxDepth).Select(p => p.Pid);

        /// <summary>Whether <paramref name="pid"/>'s parent is an editor, so the extension's host started it.</summary>
        public bool StartedByEditor(int pid) =>
            _processes.TryGetValue(pid, out var claude) && _processes.TryGetValue(claude.Parent, out var parent) && Editors.Contains(parent.Name);
    }
}
