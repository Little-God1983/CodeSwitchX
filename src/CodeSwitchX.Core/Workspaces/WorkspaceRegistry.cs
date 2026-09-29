using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Paths;
using CodeSwitchX.Core.Persistence;

namespace CodeSwitchX.Core.Workspaces;

/// <summary>Single entry point for changing registered workspaces: persists, refreshes the resolver, notifies the bus.</summary>
public sealed class WorkspaceRegistry
{
    private readonly IWorkspaceStore _store;
    private readonly WorkspaceResolver _resolver;
    private readonly IEventBus _bus;

    public WorkspaceRegistry(IWorkspaceStore store, WorkspaceResolver resolver, IEventBus bus)
    {
        _store = store;
        _resolver = resolver;
        _bus = bus;
    }

    public async Task<IReadOnlyList<Workspace>> LoadAsync(CancellationToken ct)
    {
        var workspaces = await _store.GetAllAsync(ct).ConfigureAwait(false);
        _resolver.SetRoots(WorkspaceResolver.RootsOf(workspaces));
        _bus.Publish(new WorkspaceRootsChanged());
        return workspaces;
    }

    public async Task RegisterAsync(Workspace workspace, CancellationToken ct)
    {
        // Stored in the user's casing: this path is launched, shown and used as a terminal cwd. Claude Code keys its
        // project state by the exact cwd string, so lower-casing it would split resume history and project memory.
        workspace.RootPath = PathNormalizer.Canonical(workspace.RootPath);
        foreach (var worktree in workspace.Worktrees)
        {
            worktree.WorkspaceId = workspace.Id;
            worktree.Path = PathNormalizer.Canonical(worktree.Path);
        }

        await _store.AddAsync(workspace, ct).ConfigureAwait(false);

        // Announced before the roots change: the engine re-maps chats into the new workspace as soon as they do,
        // and the Yard can only move a chat onto a tile that already exists.
        _bus.Publish(new WorkspaceRegistered(workspace));
        await LoadAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the workspace's worktrees with the ones git lists now, when they differ: a worktree added after the
    /// registration becomes a child root, a removed one stops being one, and a moved branch is noted. Saved and reloaded
    /// like a registration, so the engine re-maps chats at once. A worktree that stays keeps its row. Returns whether
    /// anything changed.
    /// </summary>
    public async Task<bool> UpdateWorktreesAsync(Workspace workspace, IReadOnlyList<WorktreeInfo> found, CancellationToken ct)
    {
        var current = workspace.Worktrees.ToDictionary(w => PathNormalizer.Normalize(w.Path), StringComparer.Ordinal);
        var next = new List<Worktree>();
        var changed = false;
        foreach (var info in found)
        {
            var path = PathNormalizer.Canonical(info.Path);
            if (current.Remove(PathNormalizer.Normalize(path), out var existing))
            {
                changed |= existing.Path != path || existing.Branch != info.Branch;
                existing.WorkspaceId = workspace.Id;
                existing.Path = path;
                existing.Branch = info.Branch;
                next.Add(existing);
            }
            else
            {
                changed = true;
                next.Add(new Worktree { WorkspaceId = workspace.Id, Path = path, Branch = info.Branch });
            }
        }

        changed |= current.Count > 0;
        if (!changed)
        {
            return false;
        }

        workspace.Worktrees = next;
        await _store.UpdateAsync(workspace, ct).ConfigureAwait(false);
        await LoadAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task UnregisterAsync(Guid workspaceId, CancellationToken ct)
    {
        await _store.RemoveAsync(workspaceId, ct).ConfigureAwait(false);
        await LoadAsync(ct).ConfigureAwait(false);
        _bus.Publish(new WorkspaceUnregistered(workspaceId));
    }
}
