namespace CodeSwitchX.Hosting;

public enum HostState
{
    NotStarted,
    Starting,
    Running,
    Stopped,
}

public sealed record HostStateChanged(Guid WorkspaceId, HostState State, nint Hwnd, string? Error);
