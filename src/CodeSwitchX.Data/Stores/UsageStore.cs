using CodeSwitchX.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CodeSwitchX.Data.Stores;

public sealed class UsageStore : IUsageStore
{
    private readonly IDbContextFactory<CodeSwitchXDbContext> _factory;

    public UsageStore(IDbContextFactory<CodeSwitchXDbContext> factory)
    {
        _factory = factory;
    }

    public Task AddUsageAsync(IReadOnlyCollection<UsageBucket> deltas, CancellationToken ct = default) => CommitAsync(deltas, [], ct);

    public Task UpsertCursorsAsync(IReadOnlyCollection<TranscriptCursor> cursors, CancellationToken ct = default) => CommitAsync([], cursors, ct);

    /// <summary>
    /// One DbContext, one SaveChanges: EF Core wraps it in a single SQLite transaction, so a transcript cursor never
    /// advances without the usage it covers, and usage is never counted twice because its cursor was lost.
    /// </summary>
    public async Task CommitAsync(IReadOnlyCollection<UsageBucket> deltas, IReadOnlyCollection<TranscriptCursor> cursors, CancellationToken ct = default)
    {
        if (deltas.Count == 0 && cursors.Count == 0)
        {
            return;
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        await ApplyUsageAsync(db, deltas, ct);
        await ApplyCursorsAsync(db, cursors, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<UsageBucket>> GetBucketsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.UsageBuckets.AsNoTracking()
            .Where(b => b.MinuteUtc >= fromUtc && b.MinuteUtc < toUtc)
            .OrderBy(b => b.MinuteUtc)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TranscriptCursor>> GetCursorsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TranscriptCursors.AsNoTracking().ToListAsync(ct);
    }

    private static async Task ApplyUsageAsync(CodeSwitchXDbContext db, IReadOnlyCollection<UsageBucket> deltas, CancellationToken ct)
    {
        foreach (var delta in deltas)
        {
            var minute = UsageBucket.FloorToMinute(delta.MinuteUtc);
            var existing = await db.UsageBuckets.FindAsync([delta.SessionId, delta.Model, minute], ct);
            if (existing is null)
            {
                db.UsageBuckets.Add(new UsageBucket
                {
                    SessionId = delta.SessionId,
                    Model = delta.Model,
                    MinuteUtc = minute,
                    Input = delta.Input,
                    Output = delta.Output,
                    CacheWrite = delta.CacheWrite,
                    CacheRead = delta.CacheRead,
                });
            }
            else
            {
                existing.Input += delta.Input;
                existing.Output += delta.Output;
                existing.CacheWrite += delta.CacheWrite;
                existing.CacheRead += delta.CacheRead;
            }
        }
    }

    private static async Task ApplyCursorsAsync(CodeSwitchXDbContext db, IReadOnlyCollection<TranscriptCursor> cursors, CancellationToken ct)
    {
        if (cursors.Count == 0)
        {
            return;
        }

        var paths = cursors.Select(c => c.Path).ToArray();
        var existing = await db.TranscriptCursors.Where(c => paths.Contains(c.Path)).ToDictionaryAsync(c => c.Path, ct);
        foreach (var cursor in cursors)
        {
            if (existing.TryGetValue(cursor.Path, out var row))
            {
                row.ByteOffset = cursor.ByteOffset;
                row.LastWriteUtc = cursor.LastWriteUtc;
                row.SessionId = cursor.SessionId;
            }
            else
            {
                var added = new TranscriptCursor
                {
                    Path = cursor.Path, ByteOffset = cursor.ByteOffset, LastWriteUtc = cursor.LastWriteUtc, SessionId = cursor.SessionId,
                };
                db.TranscriptCursors.Add(added);
                existing[cursor.Path] = added;
            }
        }
    }
}
