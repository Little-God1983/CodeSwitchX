using System.Runtime.InteropServices;

namespace CodeSwitchX.Hook;

internal readonly record struct ProcessInfo(int Pid, string Name);

/// <summary>Parent-process walk via Toolhelp32; AOT-safe (LibraryImport, blittable struct).</summary>
internal static unsafe partial class ProcessChain
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly nint InvalidHandle = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        public fixed char szExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(nint hSnapshot, PROCESSENTRY32W* lppe);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(nint hSnapshot, PROCESSENTRY32W* lppe);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    internal static IReadOnlyList<ProcessInfo> Ancestors(int maxDepth)
    {
        try
        {
            return Ancestors(Environment.ProcessId, Snapshot(), maxDepth);
        }
        catch
        {
            return [];
        }
    }

    internal static IReadOnlyList<ProcessInfo> Ancestors(int startPid, IReadOnlyDictionary<int, (int Parent, string Name)> table, int maxDepth)
    {
        var chain = new List<ProcessInfo>();
        var visited = new HashSet<int> { startPid };
        var current = startPid;
        while (chain.Count < maxDepth && table.TryGetValue(current, out var entry) && entry.Parent != 0 && visited.Add(entry.Parent))
        {
            if (!table.TryGetValue(entry.Parent, out var parent))
            {
                break;
            }

            chain.Add(new ProcessInfo(entry.Parent, parent.Name));
            current = entry.Parent;
        }

        return chain;
    }

    private static Dictionary<int, (int Parent, string Name)> Snapshot()
    {
        var table = new Dictionary<int, (int, string)>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandle || snapshot == 0)
        {
            return table;
        }

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
            if (!Process32FirstW(snapshot, &entry))
            {
                return table;
            }

            do
            {
                var name = new string(entry.szExeFile);
                table[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, name);
            }
            while (Process32NextW(snapshot, &entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return table;
    }
}
