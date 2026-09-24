namespace CodeSwitchX.Core.Sessions;

public interface IProcessProbe
{
    bool IsAlive(int pid);
}
