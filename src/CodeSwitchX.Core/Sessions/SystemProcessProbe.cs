using System.ComponentModel;
using System.Diagnostics;

namespace CodeSwitchX.Core.Sessions;

public sealed class SystemProcessProbe : IProcessProbe
{
    public bool IsAlive(int pid, DateTimeOffset seenAt)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            // StartTime is local time; ToUniversalTime keeps the DST flag FromFileTime set, so the fall-back hour converts right.
            return !process.HasExited && process.StartTime.ToUniversalTime() <= seenAt.UtcDateTime;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            // The PID exists but belongs to a process we may not open (protected or SYSTEM, e.g. after PID reuse):
            // no evidence that the chat died, so keep it alive rather than flag a false Errored.
            return true;
        }
    }
}
