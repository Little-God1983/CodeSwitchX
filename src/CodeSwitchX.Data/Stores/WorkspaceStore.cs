using CodeSwitchX.Core.Paths;
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

    /// <summary>Case-insensitive lookup: paths are stored in the user's casing but compared by their normalised key.</summary>
    public async Task<Workspace?> FindByTargetAsync(string rootPath, string? workspaceFile, CancellationToken ct = default)
    {
        var key = Workspace.TargetKey(rootPath, workspaceFile);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var candidates = await db.Workspaces.AsNoTracking().ToListAsync(ct);
        return candidates.FirstOrDefault(w => Workspace.TargetKey(w.RootPath, w.WorkspaceFile) == key);
    }

    public async Task AddAsync(Workspace workspace, CancellationToken ct = default)
    {
        workspace.RootPath = PathNormalizer.Canonical(workspace.RootPath);
        workspace.WorkspaceFile = workspace.WorkspaceFile is { Length: > 0 } file ? PathNormalizer.Canonical(file) : null;
        var key = Workspace.TargetKey(workspace.RootPath, workspace.WorkspaceFile);
        await using var db = await _factory.CreateDbContextAsync(ct);

        // No unique index can hold the key (it is compared normalised), so the check and the insert share one transaction:
        // SQLite begins it IMMEDIATE, taking the write lock before the check reads, and a racing add waits and then sees this row.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var targets = await db.Workspaces.Select(w => new { w.RootPath, w.WorkspaceFile, w.Number }).ToListAsync(ct);
        if (targets.Any(t => Workspace.TargetKey(t.RootPath, t.WorkspaceFile) == key))
        {
            throw new DuplicateWorkspaceException(workspace.Target);
        }

        // In the same transaction, so two adds never take the same number.
        workspace.Number = LowestFreeNumber(targets.Select(t => t.Number));
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task UpdateAsync(Workspace workspace, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        // Read and written in one transaction: an edit and a worktree refresh at once do not work on each other's stale rows (#273).
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == workspace.Id, ct)
            ?? throw new KeyNotFoundException($"Workspace {workspace.Id} not found.");

        var number = existing.Number;
        db.Entry(existing).CurrentValues.SetValues(workspace);
        existing.Number = number; // given once by AddAsync, never changed by an edit
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
        await transaction.CommitAsync(ct);
    }

    public async Task ReplaceWorktreesAsync(Guid workspaceId, IReadOnlyList<Worktree> worktrees, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct); // as UpdateAsync (#273)
        var existing = await db.Worktrees.Where(t => t.WorkspaceId == workspaceId).ToListAsync(ct);
        db.Worktrees.RemoveRange(existing.Where(t => worktrees.All(n => n.Id != t.Id)));
        foreach (var worktree in worktrees)
        {
            var current = existing.FirstOrDefault(t => t.Id == worktree.Id);
            if (current is null)
            {
                db.Worktrees.Add(new Worktree { Id = worktree.Id, WorkspaceId = workspaceId, Path = worktree.Path, Branch = worktree.Branch });
            }
            else
            {
                current.Path = worktree.Path;
                current.Branch = worktree.Branch;
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>The lowest number from 1 not in <paramref name="taken"/> (see <see cref="Workspace.Number"/>).</summary>
    private static int LowestFreeNumber(IEnumerable<int> taken)
    {
        var used = taken.ToHashSet();
        var number = 1;
        while (used.Contains(number))
        {
            number++;
        }

        return number;
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
        // The read and the insert in one transaction, begun IMMEDIATE: a track added at the same time waits, and then sees
        // this one's order (#273: a read no longer waits for a write in progress).
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var maxOrder = await db.Tracks.Select(t => (int?)t.SortOrder).MaxAsync(ct) ?? -1;
        var track = new Track { Name = name, SortOrder = maxOrder + 1 };
        db.Tracks.Add(track);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return track;
    }

    public async Task UpdateTrackAsync(Track track, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Tracks.Update(track);
        await db.SaveChangesAsync(ct);
    }
}
