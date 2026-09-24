namespace CodeSwitchX.Core.Workspaces;

public interface IWorkspaceResolver
{
    /// <summary>Maps a working directory to the workspace whose root (or worktree) contains it, longest root first.</summary>
    Guid? Resolve(string? path);
}
