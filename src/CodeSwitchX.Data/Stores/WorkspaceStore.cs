using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace CodeSwitchX.Data.Stores;

public sealed class WorkspaceStore : IWorkspaceStore
{
    private readonly IDbContextFactory<CodeSwitchXDbContext> _factory;

    public WorkspaceStore(IDbContextFactory<CodeSwitchXDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<Workspace>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Workspaces.AsNoTracking().OrderBy(w => w.Name).ToListAsync(ct);
    }

    public async Task<Workspace?> FindByRootAsync(string normalizedRoot, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.RootPath == normalizedRoot, ct);
    }

    public async Task AddAsync(Workspace workspace, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        if (await db.Workspaces.AnyAsync(w => w.RootPath == workspace.RootPath, ct))
        {
            throw new DuplicateWorkspaceException(workspace.RootPath);
        }

        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Workspace workspace, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == workspace.Id, ct)
            ?? throw new KeyNotFoundException($"Workspace {workspace.Id} not found.");

        db.Entry(existing).CurrentValues.SetValues(workspace);
        existing.Worktrees.RemoveAll(t => workspace.Worktrees.All(n => n.Id != t.Id));
        foreach (var worktree in workspace.Worktrees)
        {
            var current = existing.Worktrees.FirstOrDefault(t => t.Id == worktree.Id);
            if (current is null)
            {
                // The key is already set client-side, so EF would treat a navigation-discovered entity as Modified; add it explicitly.
                var added = new Worktree { Id = worktree.Id, WorkspaceId = existing.Id, Path = worktree.Path, Branch = worktree.Branch };
                existing.Worktrees.Add(added);
                db.Worktrees.Add(added);
            }
            else
            {
                current.Path = worktree.Path;
                current.Branch = worktree.Branch;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(Guid workspaceId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Workspaces.Where(w => w.Id == workspaceId).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<Track>> GetTracksAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Tracks.AsNoTracking().OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct);
    }

    public async Task<Track> AddTrackAsync(string name, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var maxOrder = await db.Tracks.Select(t => (int?)t.SortOrder).MaxAsync(ct) ?? -1;
        var track = new Track { Name = name, SortOrder = maxOrder + 1 };
        db.Tracks.Add(track);
        await db.SaveChangesAsync(ct);
        return track;
    }

    public async Task UpdateTrackAsync(Track track, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Tracks.Update(track);
        await db.SaveChangesAsync(ct);
    }
}
