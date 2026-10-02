using System.ComponentModel;
using System.Diagnostics;

namespace CodeSwitchX.Core.Sessions;

public sealed class SystemProcessProbe : IProcessProbe
{
    public bool IsAlive(int pid, DateTimeOffset seenAt)
    {
        try
        {
            return StartOf(pid) is { } start && DateTime.FromFileTimeUtc(start) <= seenAt.UtcDateTime;
        }
        catch (Win32Exception)
        {
            // The PID exists but belongs to a process we may not open (protected or SYSTEM, e.g. after PID reuse):
            // no evidence that the chat died, so keep it alive rather than flag a false Errored.
            return true;
        }
    }

    /// <summary>
    /// When the process with the id started, as a UTC file time; null when none runs. A process that may not be opened
    /// (protected or SYSTEM) throws <see cref="Win32Exception"/>: whether that means alive is the caller's to say.
    /// </summary>
    public static long? StartOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            // StartTime is local time; ToFileTimeUtc keeps the DST flag FromFileTime set, so the fall-back hour converts right.
            return process.HasExited ? null : process.StartTime.ToFileTimeUtc();
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
