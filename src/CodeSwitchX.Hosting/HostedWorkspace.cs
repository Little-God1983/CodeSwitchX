using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.Hosting;

/// <summary>Mutable per-workspace host record. Only <see cref="HostManager"/> writes to it.</summary>
public sealed class HostedWorkspace
{
    public HostedWorkspace(Guid workspaceId)
    {
        WorkspaceId = workspaceId;
    }

    public Guid WorkspaceId { get; }
    public HostState State { get; internal set; } = HostState.NotStarted;
    public nint Hwnd { get; internal set; }
    public uint ProcessId { get; internal set; }
    public bool Visible { get; internal set; }
    public ScreenRect? TargetRect { get; internal set; }
    public string? Error { get; internal set; }
    public DateTimeOffset? StartedAt { get; internal set; }
}
