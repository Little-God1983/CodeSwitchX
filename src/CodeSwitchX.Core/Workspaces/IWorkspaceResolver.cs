namespace CodeSwitchX.Core.Workspaces;

public interface IWorkspaceResolver
{
    /// <summary>
    /// Maps a working directory to the workspace whose root (or worktree) contains it, longest root first. With
    /// <paramref name="windowFolders"/>, the folders of the VS Code window the chat runs in, the workspace whose folders
    /// are most like the window's comes first: two workspaces can share a folder, and only the window tells them apart.
    /// </summary>
    Guid? Resolve(string? path, IReadOnlyCollection<string>? windowFolders = null);
}
