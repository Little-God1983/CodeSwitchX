using CodeSwitchX.Core.Paths;

namespace CodeSwitchX.Core.Workspaces;

public sealed class Workspace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Canonical folder that owns the tile (see <see cref="PathNormalizer.Canonical"/>): real casing, no trailing separator.
    /// Not unique: a <c>.code-workspace</c> roots at its first folder, which can be a shared repository registered on its own too.
    /// </summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Optional <c>.code-workspace</c> file to open instead of the folder.</summary>
    public string? WorkspaceFile { get; set; }

    /// <summary>What VS Code opens for this workspace (see <see cref="TargetOf"/>). Not stored: EF maps no property without a setter.</summary>
    public string Target => TargetOf(RootPath, WorkspaceFile);

    /// <summary>What VS Code opens: the <c>.code-workspace</c> file when there is one, else the folder.</summary>
    public static string TargetOf(string rootPath, string? workspaceFile) => workspaceFile is { Length: > 0 } file ? file : rootPath;

    /// <summary>What identifies a registration: its <see cref="TargetOf"/>, compared ignoring case. Two registrations with the same key are one workspace.</summary>
    public static string TargetKey(string rootPath, string? workspaceFile) => PathNormalizer.Normalize(TargetOf(rootPath, workspaceFile));

    public Guid TrackId { get; set; }
    public string AccentColor { get; set; } = "#3B82F6";
    public HostMode HostMode { get; set; } = HostMode.Snap;
    public string? VsCodeProfile { get; set; }
    public bool AutoStart { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Worktree> Worktrees { get; set; } = [];
}
