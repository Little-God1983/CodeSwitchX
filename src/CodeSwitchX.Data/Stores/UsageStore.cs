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

    public async Task AddUsageAsync(IReadOnlyCollection<UsageBucket> deltas, CancellationToken ct = default)
    {
        if (deltas.Count == 0)
        {
            return;
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
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

    public async Task UpsertCursorsAsync(IReadOnlyCollection<TranscriptCursor> cursors, CancellationToken ct = default)
    {
        if (cursors.Count == 0)
        {
            return;
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
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
                db.TranscriptCursors.Add(new TranscriptCursor
                {
                    Path = cursor.Path, ByteOffset = cursor.ByteOffset, LastWriteUtc = cursor.LastWriteUtc, SessionId = cursor.SessionId,
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
