using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace CodeSwitchX.Hosting.Win32;

/// <summary>Who runs under whom, and who listens where: the two system tables <see cref="VsCode.ClaudeIdeWindows"/> reads.</summary>
internal static unsafe class ProcessTable
{
    private const uint Th32csSnapProcess = 0x00000002;
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32W
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

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint hSnapshot, ProcessEntry32W* lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint hSnapshot, ProcessEntry32W* lppe);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(void* pTcpTable, ref uint pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf,
        int tableClass, uint reserved);

    /// <summary>The processes above <paramref name="pid"/>, its parent first, at most <paramref name="maxDepth"/>; none when it is gone.</summary>
    public static IReadOnlyList<int> Ancestors(int pid, int maxDepth)
    {
        var parents = Parents();
        var chain = new List<int>();
        var seen = new HashSet<int> { pid };
        var current = pid;
        while (chain.Count < maxDepth && parents.TryGetValue(current, out var parent) && parent != 0 && parents.ContainsKey(parent) && seen.Add(parent))
        {
            chain.Add(parent);
            current = parent;
        }

        return chain;
    }

    /// <summary>The process listening on each IPv4 TCP port; Claude Code's VS Code extension listens on 127.0.0.1.</summary>
    public static IReadOnlyDictionary<int, int> LoopbackListeners()
    {
        uint size = 0;
        var result = GetExtendedTcpTable(null, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        for (var attempt = 0; attempt < 4 && result == ErrorInsufficientBuffer; attempt++)
        {
            size += 1024; // the table can grow between the two calls
            var buffer = (byte*)NativeMemory.Alloc(size);
            try
            {
                result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
                if (result == 0)
                {
                    var count = *(uint*)buffer;
                    var rows = (TcpRowOwnerPid*)(buffer + sizeof(uint));
                    var listeners = new Dictionary<int, int>();
                    for (var i = 0; i < count; i++)
                    {
                        // The port is in network byte order in the low 16 bits.
                        listeners.TryAdd((ushort)IPAddress.NetworkToHostOrder((short)rows[i].dwLocalPort), (int)rows[i].dwOwningPid);
                    }

                    return listeners;
                }
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        throw new Win32Exception((int)result);
    }

    private static Dictionary<int, int> Parents()
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == -1)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            var parents = new Dictionary<int, int>();
            var entry = new ProcessEntry32W { dwSize = (uint)sizeof(ProcessEntry32W) };
            for (var more = Process32FirstW(snapshot, &entry); more; more = Process32NextW(snapshot, &entry))
            {
                parents[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
            }

            return parents;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }
}
