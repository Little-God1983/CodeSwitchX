using CodeSwitchX.Core.Paths;

namespace CodeSwitchX.Core.Workspaces;

public sealed class WorkspaceResolver : IWorkspaceResolver
{
    private volatile WorkspaceRoot[] _roots = [];
    private volatile (Guid WorkspaceId, HashSet<string> Folders)[] _windows = [];

    /// <summary>
    /// Longest root first; the sort is stable, so of two equal paths the one listed first wins. <paramref name="windows"/>
    /// (from <see cref="WindowsOf"/>) keep their order: of two workspaces that open the same folders, the first wins.
    /// </summary>
    public void SetRoots(IEnumerable<WorkspaceRoot> roots, IEnumerable<WorkspaceWindow>? windows = null)
    {
        _roots = roots
            .Select(r => new WorkspaceRoot(r.WorkspaceId, PathNormalizer.Normalize(r.Path)))
            .OrderByDescending(r => r.Path.Length)
            .ToArray();
        _windows = (windows ?? [])
            .Select(w => (w.WorkspaceId, Folders: FolderSet(w.Folders)))
            .Where(w => w.Folders is { Count: > 0 })
            .Select(w => (w.WorkspaceId, w.Folders!))
            .ToArray();
    }

    /// <summary>
    /// The folders VS Code opens for each workspace, in the order of <see cref="RootsOf"/>: a folder workspace opens its
    /// root, a <c>.code-workspace</c> the folders it lists (read with <paramref name="foldersOf"/>; none without it, or
    /// when the file cannot be read).
    /// </summary>
    public static IEnumerable<WorkspaceWindow> WindowsOf(IEnumerable<Workspace> workspaces, Func<string, IReadOnlyList<WorkspaceFolder>?>? foldersOf = null)
    {
        foreach (var workspace in workspaces.OrderBy(w => w.WorkspaceFile is { Length: > 0 }).ThenBy(w => w.CreatedAt).ThenBy(w => w.Id))
        {
            if (workspace.WorkspaceFile is not { Length: > 0 } file)
            {
                yield return new WorkspaceWindow(workspace.Id, [workspace.RootPath]);
            }
            else if (foldersOf?.Invoke(file) is { Count: > 0 } folders)
            {
                yield return new WorkspaceWindow(workspace.Id, folders.Select(f => f.Path).ToList());
            }
        }
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

    public Guid? Resolve(string? path, IReadOnlyCollection<string>? windowFolders = null)
    {
        var normalized = Normalized(path);
        var roots = _roots;
        if (windowFolders is { Count: > 0 } && FolderSet(windowFolders) is { } window && ByWindow(normalized, window, roots) is { } byWindow)
        {
            return byWindow;
        }

        if (normalized is null)
        {
            return null;
        }

        foreach (var root in roots)
        {
            if (PathNormalizer.IsWithin(normalized, root.Path))
            {
                return root.WorkspaceId;
            }
        }

        return null;
    }

    /// <summary>
    /// Of the workspaces that hold <paramref name="path"/>, the one whose folders are most like the window's (shared
    /// folders over all folders of both), so a window the user added a folder to, or took one from, still finds its
    /// workspace; ties go to the one listed first. A path no workspace holds takes only a workspace that opens exactly
    /// the window's folders. Null when no workspace shares a folder with the window.
    /// </summary>
    private Guid? ByWindow(string? path, HashSet<string> window, WorkspaceRoot[] roots)
    {
        var holders = path is null ? [] : roots.Where(r => PathNormalizer.IsWithin(path, r.Path)).Select(r => r.WorkspaceId).ToHashSet();
        Guid? best = null;
        var bestLikeness = 0.0;
        foreach (var (workspaceId, folders) in _windows)
        {
            if (holders.Count > 0 ? !holders.Contains(workspaceId) : !folders.SetEquals(window))
            {
                continue;
            }

            var shared = folders.Count(window.Contains);
            var likeness = (double)shared / (folders.Count + window.Count - shared);
            if (likeness > bestLikeness)
            {
                best = workspaceId;
                bestLikeness = likeness;
            }
        }

        return best;
    }

    private static string? Normalized(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return PathNormalizer.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>The folders as comparison keys; null when one of them is no path.</summary>
    private static HashSet<string>? FolderSet(IEnumerable<string> folders)
    {
        try
        {
            return folders.Select(PathNormalizer.Normalize).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
