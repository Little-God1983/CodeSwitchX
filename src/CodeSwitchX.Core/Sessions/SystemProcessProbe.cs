using System.Diagnostics;

namespace CodeSwitchX.Core.Sessions;

public sealed class SystemProcessProbe : IProcessProbe
{
    public bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
