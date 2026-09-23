namespace CodeSwitchX.Core.Workspaces;

public sealed class Worktree
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }

    /// <summary>Normalised path of the worktree; a child root for session mapping.</summary>
    public string Path { get; set; } = string.Empty;
    public string? Branch { get; set; }
}
