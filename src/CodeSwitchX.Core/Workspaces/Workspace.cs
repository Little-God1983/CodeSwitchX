namespace CodeSwitchX.Core.Workspaces;

public sealed class Workspace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    /// <summary>Canonical folder that owns the tile (see <see cref="Paths.PathNormalizer.Canonical"/>): real casing, no trailing separator. Unique ignoring case.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Optional <c>.code-workspace</c> file to open instead of the folder.</summary>
    public string? WorkspaceFile { get; set; }

    public Guid TrackId { get; set; }
    public string AccentColor { get; set; } = "#3B82F6";
    public HostMode HostMode { get; set; } = HostMode.Snap;
    public string? VsCodeProfile { get; set; }
    public bool AutoStart { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Worktree> Worktrees { get; set; } = [];
}
