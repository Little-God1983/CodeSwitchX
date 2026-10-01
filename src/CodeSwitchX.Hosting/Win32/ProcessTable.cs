using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using CodeSwitchX.Hook;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.IpHelper;

namespace CodeSwitchX.Hosting.Win32;

/// <summary>Who runs under whom, and who listens where: the two system tables <see cref="VsCode.ClaudeIdeWindows"/> reads.</summary>
internal static unsafe class ProcessTable
{
    private const uint AfInet = 2;
    private const uint AfInet6 = 23;

    /// <summary>
    /// Every local TCP port with each process listening on it, IPv4 and IPv6: Claude Code's VS Code extension listens on
    /// 127.0.0.1 today, a listener on "::" shows in the IPv6 table only, and two processes can hold one port number on
    /// the two stacks. Throws when the IPv4 table cannot be read; a missing IPv6 stack leaves the IPv4 listeners.
    /// </summary>
    public static IReadOnlyList<(int Port, int Pid)> Listeners()
    {
        var listeners = new List<(int Port, int Pid)>();
        var v4 = Read(AfInet, table =>
        {
            var rows = (MIB_TCPTABLE_OWNER_PID*)table;
            foreach (var row in rows->table.AsSpan((int)rows->dwNumEntries))
            {
                listeners.Add((PortOf(row.dwLocalPort), (int)row.dwOwningPid));
            }
        });
        if (v4 != WIN32_ERROR.NO_ERROR)
        {
            throw new Win32Exception((int)v4);
        }

        Read(AfInet6, table =>
        {
            var rows = (MIB_TCP6TABLE_OWNER_PID*)table;
            foreach (var row in rows->table.AsSpan((int)rows->dwNumEntries))
            {
                listeners.Add((PortOf(row.dwLocalPort), (int)row.dwOwningPid));
            }
        });
        return listeners;
    }

    /// <summary>Every process with its parent, from the snapshot the hook takes too. Throws when the system refuses the snapshot.</summary>
    public static IReadOnlyDictionary<int, (int Parent, string Name)> Processes()
    {
        var table = ProcessChain.Snapshot();
        return table.Count > 0 ? table : throw new Win32Exception("The process snapshot could not be taken.");
    }

    /// <summary>The port is in network byte order in the low 16 bits of its field.</summary>
    private static int PortOf(uint field) => (ushort)IPAddress.NetworkToHostOrder((short)field);

    private static WIN32_ERROR Read(uint family, Action<nint> rows)
    {
        uint size = 0;
        var result = (WIN32_ERROR)PInvoke.GetExtendedTcpTable(Span<byte>.Empty, ref size, false, family, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_LISTENER, 0);
        for (var attempt = 0; attempt < 4 && result == WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER; attempt++)
        {
            var buffer = new byte[size + 1024]; // the table can grow between the two calls
            size = (uint)buffer.Length;
            result = (WIN32_ERROR)PInvoke.GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (result == WIN32_ERROR.NO_ERROR)
            {
                fixed (byte* table = buffer)
                {
                    rows((nint)table);
                }
            }
        }

        return result;
    }
}
