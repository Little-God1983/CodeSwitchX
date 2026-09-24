namespace CodeSwitchX.Core.Sessions;

public interface IProcessProbe
{
    /// <summary>
    /// True while the process that had <paramref name="pid"/> at <paramref name="seenAt"/> still runs. Windows reuses
    /// process ids, so a process with that id that started after <paramref name="seenAt"/> is a different one.
    /// </summary>
    bool IsAlive(int pid, DateTimeOffset seenAt);
}
