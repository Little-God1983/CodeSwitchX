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

    /// <summary>The name VS Code puts in this workspace's window title (see <see cref="VsCode.VsCodeLauncher.DisplayNameForMatching"/>).</summary>
    public string DisplayName { get; internal set; } = string.Empty;

    /// <summary>The VS Code profile the workspace is opened with, if it names one.</summary>
    public string? Profile { get; internal set; }

    public HostState State { get; internal set; } = HostState.NotStarted;
    public nint Hwnd { get; internal set; }
    public uint ProcessId { get; internal set; }
    public bool Visible { get; internal set; }
    public ScreenRect? TargetRect { get; internal set; }
    public string? Error { get; internal set; }
    public DateTimeOffset? StartedAt { get; internal set; }

    internal ScreenRect? SnapBackFrom { get; set; }
    internal DateTimeOffset LastSnapBackAt { get; set; }
    internal int SnapBackRepeats { get; set; }
    internal bool SnapBackSuspended => SnapBackRepeats > HostManager.SnapBackLimit;

    internal void ResetSnapBack()
    {
        SnapBackFrom = null;
        SnapBackRepeats = 0;
    }
}
