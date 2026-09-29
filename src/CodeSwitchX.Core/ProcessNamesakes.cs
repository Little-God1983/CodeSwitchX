using System.Diagnostics;

namespace CodeSwitchX.Core;

/// <summary>
/// The other processes with this process's image name in this Windows session. Windows belong to a session: an instance
/// in another session, of this or another account (a disconnected one on a shared workstation), cannot see this session's
/// windows, so it neither hid them nor can answer for them.
/// </summary>
public static class ProcessNamesakes
{
    public static IReadOnlyList<int> InThisSession()
    {
        using var self = Process.GetCurrentProcess();
        var namesakes = Process.GetProcessesByName(self.ProcessName);
        try
        {
            return InSession(namesakes.Select(p => (p.Id, p.SessionId)).ToList(), self.Id, self.SessionId);
        }
        finally
        {
            foreach (var process in namesakes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>The ids of the namesakes other than this process that share its session.</summary>
    public static IReadOnlyList<int> InSession(IEnumerable<(int Pid, int SessionId)> namesakes, int selfPid, int selfSessionId) =>
        namesakes.Where(p => p.Pid != selfPid && p.SessionId == selfSessionId).Select(p => p.Pid).ToList();
}
