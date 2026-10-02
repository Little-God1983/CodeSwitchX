using System.ComponentModel;

namespace CodeSwitchX.Hosting.Win32;

/// <summary>Who started a process, from the system's process snapshot.</summary>
public static class ProcessParents
{
    /// <summary>The parent of a running process; null when it is gone or the snapshot cannot be taken.</summary>
    public static int? ParentOf(int pid)
    {
        try
        {
            return ProcessTable.Processes().TryGetValue(pid, out var process) ? process.Parent : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}
