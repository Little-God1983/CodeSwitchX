using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Core.Persistence;

public interface IWorkspaceStore
{
    Task<IReadOnlyList<Workspace>> GetAllAsync(CancellationToken ct = default);
    Task<Workspace?> FindByRootAsync(string normalizedRoot, CancellationToken ct = default);
    Task AddAsync(Workspace workspace, CancellationToken ct = default);
    Task UpdateAsync(Workspace workspace, CancellationToken ct = default);

    /// <summary>Replaces the workspace's worktree rows and nothing else: a row whose id is in the list is updated, the others are removed, new ones added.</summary>
    Task ReplaceWorktreesAsync(Guid workspaceId, IReadOnlyList<Worktree> worktrees, CancellationToken ct = default);
    Task RemoveAsync(Guid workspaceId, CancellationToken ct = default);
    Task<IReadOnlyList<Track>> GetTracksAsync(CancellationToken ct = default);
    Task<Track> AddTrackAsync(string name, CancellationToken ct = default);
    Task UpdateTrackAsync(Track track, CancellationToken ct = default);
}
