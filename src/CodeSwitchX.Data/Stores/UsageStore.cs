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

    public Task AddUsageAsync(IReadOnlyCollection<UsageBucket> deltas, CancellationToken ct = default) => CommitAsync(deltas, [], [], ct);

    public Task UpsertCursorsAsync(IReadOnlyCollection<TranscriptCursor> cursors, CancellationToken ct = default) => CommitAsync([], cursors, [], ct);

    /// <summary>
    /// One DbContext, one SaveChanges: EF Core wraps it in a single SQLite transaction, so a transcript cursor never
    /// advances without the usage it covers, usage is never counted twice because its cursor was lost, and a message
    /// id is never remembered without its usage (or the other way round).
    /// </summary>
    public async Task CommitAsync(IReadOnlyCollection<UsageBucket> deltas, IReadOnlyCollection<TranscriptCursor> cursors, IReadOnlyCollection<string> messageIds,
        CancellationToken ct = default)
    {
        if (deltas.Count == 0 && cursors.Count == 0 && messageIds.Count == 0)
        {
            return;
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        await ApplyUsageAsync(db, deltas, ct);
        await ApplyCursorsAsync(db, cursors, ct);
        await ApplySeenMessagesAsync(db, messageIds, ct);
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

    public async Task<IReadOnlyList<string>> GetSeenMessageIdsAsync(int limit, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var newestFirst = await db.SeenMessages.AsNoTracking()
            .OrderByDescending(m => m.Seq)
            .Take(limit)
            .Select(m => m.MessageId)
            .ToListAsync(ct);
        newestFirst.Reverse();
        return newestFirst;
    }

    public async Task<int> PruneSeenMessagesAsync(int keep, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var newestToDrop = await db.SeenMessages.OrderByDescending(m => m.Seq).Skip(keep).Select(m => (long?)m.Seq).FirstOrDefaultAsync(ct);
        return newestToDrop is { } cutoff
            ? await db.SeenMessages.Where(m => m.Seq <= cutoff).ExecuteDeleteAsync(ct)
            : 0;
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

    /// <summary>Ids are numbered explicitly in commit order, so "the newest N" is well defined without relying on autoincrement.</summary>
    private static async Task ApplySeenMessagesAsync(CodeSwitchXDbContext db, IReadOnlyCollection<string> messageIds, CancellationToken ct)
    {
        if (messageIds.Count == 0)
        {
            return;
        }

        var wanted = messageIds.Distinct(StringComparer.Ordinal).ToArray();
        var known = (await db.SeenMessages.Where(m => wanted.Contains(m.MessageId)).Select(m => m.MessageId).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var next = (await db.SeenMessages.MaxAsync(m => (long?)m.Seq, ct) ?? 0) + 1;
        foreach (var id in wanted)
        {
            if (!known.Contains(id))
            {
                db.SeenMessages.Add(new SeenMessage { Seq = next++, MessageId = id });
            }
        }
    }
}
