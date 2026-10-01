using System.ComponentModel;
using System.Runtime.InteropServices;
using CodeSwitchX.Hook;

namespace CodeSwitchX.Hosting.Win32;

/// <summary>Who runs under whom, and who listens where: the two system tables <see cref="VsCode.ClaudeIdeWindows"/> reads.</summary>
internal static unsafe class ProcessTable
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const uint ErrorInsufficientBuffer = 122;

    /// <summary>MIB_TCPROW_OWNER_PID: state, local address, local port, remote address, remote port, owning process.</summary>
    private static readonly (int RowSize, int PortOffset, int PidOffset) V4Row = (24, 8, 20);

    /// <summary>MIB_TCP6ROW_OWNER_PID: local address (16), scope, local port, remote address (16), scope, port, state, owning process.</summary>
    private static readonly (int RowSize, int PortOffset, int PidOffset) V6Row = (56, 20, 52);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(void* pTcpTable, ref uint pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf,
        int tableClass, uint reserved);

    /// <summary>
    /// The process listening on each local TCP port, IPv4 and IPv6: Claude Code's VS Code extension listens on 127.0.0.1
    /// today, and a listener on "::" shows in the IPv6 table only. Throws when the IPv4 table cannot be read.
    /// </summary>
    public static IReadOnlyDictionary<int, int> Listeners()
    {
        var listeners = new Dictionary<int, int>();
        var v4 = Read(AfInet, V4Row, listeners);
        if (v4 != 0)
        {
            throw new Win32Exception((int)v4);
        }

        Read(AfInet6, V6Row, listeners); // no IPv6 stack: the IPv4 listeners still tell
        return listeners;
    }

    /// <summary>Every process with its parent, from the snapshot the hook takes too. Throws when the system refuses the snapshot.</summary>
    public static IReadOnlyDictionary<int, (int Parent, string Name)> Processes()
    {
        var table = ProcessChain.Snapshot();
        return table.Count > 0 ? table : throw new Win32Exception("The process snapshot could not be taken.");
    }

    private static uint Read(int family, (int RowSize, int PortOffset, int PidOffset) row, Dictionary<int, int> into)
    {
        uint size = 0;
        var result = GetExtendedTcpTable(null, ref size, false, family, TcpTableOwnerPidListener, 0);
        for (var attempt = 0; attempt < 4 && result == ErrorInsufficientBuffer; attempt++)
        {
            size += 1024; // the table can grow between the two calls
            var buffer = (byte*)NativeMemory.Alloc(size);
            try
            {
                result = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0);
                if (result == 0)
                {
                    var count = *(uint*)buffer;
                    for (var i = 0; i < count; i++)
                    {
                        var entry = buffer + sizeof(uint) + (i * row.RowSize);
                        // The port is in network byte order in the first two bytes of its field.
                        var port = (entry[row.PortOffset] << 8) | entry[row.PortOffset + 1];
                        into.TryAdd(port, (int)*(uint*)(entry + row.PidOffset));
                    }
                }
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        return result;
    }
}
