using CodeSwitchX.Core.Paths;

namespace CodeSwitchX.Core.Workspaces;

public sealed class WorkspaceResolver : IWorkspaceResolver
{
    private volatile WorkspaceRoot[] _roots = [];

    /// <summary>Longest root first; the sort is stable, so of two equal paths the one listed first wins.</summary>
    public void SetRoots(IEnumerable<WorkspaceRoot> roots)
    {
        _roots = roots
            .Select(r => new WorkspaceRoot(r.WorkspaceId, PathNormalizer.Normalize(r.Path)))
            .OrderByDescending(r => r.Path.Length)
            .ToArray();
    }

    /// <summary>
    /// Every workspace root, then every worktree: a folder registered as its own workspace must beat the same
    /// folder found as another workspace's worktree.
    /// </summary>
    public static IEnumerable<WorkspaceRoot> RootsOf(IEnumerable<Workspace> workspaces)
    {
        var list = workspaces.ToList();
        foreach (var workspace in list)
        {
            yield return new WorkspaceRoot(workspace.Id, workspace.RootPath);
        }

        foreach (var workspace in list)
        {
            foreach (var worktree in workspace.Worktrees)
            {
                yield return new WorkspaceRoot(workspace.Id, worktree.Path);
            }
        }
    }

    public Guid? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string normalized;
        try
        {
            normalized = PathNormalizer.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        foreach (var root in _roots)
        {
            if (PathNormalizer.IsWithin(normalized, root.Path))
            {
                return root.WorkspaceId;
            }
        }

        return null;
    }
}
