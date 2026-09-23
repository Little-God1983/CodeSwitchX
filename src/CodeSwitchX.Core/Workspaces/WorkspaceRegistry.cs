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
        workspace.RootPath = PathNormalizer.Normalize(workspace.RootPath);
        foreach (var worktree in workspace.Worktrees)
        {
            worktree.WorkspaceId = workspace.Id;
            worktree.Path = PathNormalizer.Normalize(worktree.Path);
        }

        await _store.AddAsync(workspace, ct).ConfigureAwait(false);
        await LoadAsync(ct).ConfigureAwait(false);
        _bus.Publish(new WorkspaceRegistered(workspace));
    }

    public async Task UnregisterAsync(Guid workspaceId, CancellationToken ct)
    {
        await _store.RemoveAsync(workspaceId, ct).ConfigureAwait(false);
        await LoadAsync(ct).ConfigureAwait(false);
        _bus.Publish(new WorkspaceUnregistered(workspaceId));
    }
}
