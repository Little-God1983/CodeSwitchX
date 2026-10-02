using System.ComponentModel;

namespace CodeSwitchX.Hosting.Win32;

/// <summary>Who started which process, from the system's process snapshot.</summary>
public static class ProcessParents
{
    /// <summary>Every running process with its parent, from one snapshot; empty when the snapshot cannot be taken.</summary>
    public static IReadOnlyDictionary<int, int> Snapshot()
    {
        try
        {
            return ProcessTable.Processes().ToDictionary(p => p.Key, p => p.Value.Parent);
        }
        catch (Win32Exception)
        {
            return new Dictionary<int, int>();
        }
    }
}
