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
    /// Every workspace root, then the other folders each <c>.code-workspace</c> lists (read with <paramref name="foldersOf"/>;
    /// none without it, or when the file cannot be read), then every worktree: a folder registered as its own workspace must
    /// beat the same folder found as another workspace's folder or worktree. Folder workspaces come before <c>.code-workspace</c>
    /// ones for the same reason: a shared repository registered on its own owns its chats, not every multi-root workspace
    /// that lists it. Other ties go to the workspace registered first, so a rename never moves chats.
    /// </summary>
    public static IEnumerable<WorkspaceRoot> RootsOf(IEnumerable<Workspace> workspaces, Func<string, IReadOnlyList<WorkspaceFolder>?>? foldersOf = null)
    {
        var list = workspaces.OrderBy(w => w.WorkspaceFile is { Length: > 0 }).ThenBy(w => w.CreatedAt).ThenBy(w => w.Id).ToList();
        foreach (var workspace in list)
        {
            yield return new WorkspaceRoot(workspace.Id, workspace.RootPath);
        }

        foreach (var workspace in list)
        {
            if (foldersOf is not null && workspace.WorkspaceFile is { Length: > 0 } file)
            {
                foreach (var folder in foldersOf(file) ?? [])
                {
                    yield return new WorkspaceRoot(workspace.Id, folder.Path);
                }
            }
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
