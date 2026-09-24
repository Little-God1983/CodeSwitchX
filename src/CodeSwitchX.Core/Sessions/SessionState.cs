namespace CodeSwitchX.Core.Sessions;

public enum SessionState
{
    Starting,
    Idle,
    Working,
    Waiting,
    Stale,
    Errored,
    Ended,
}
