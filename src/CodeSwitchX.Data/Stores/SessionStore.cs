using CodeSwitchX.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CodeSwitchX.Data.Stores;

public sealed class SessionStore : ISessionStore
{
    private readonly IDbContextFactory<CodeSwitchXDbContext> _factory;

    public SessionStore(IDbContextFactory<CodeSwitchXDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<SessionRecord>> GetActiveSinceAsync(DateTimeOffset lastEventAfter, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Sessions.AsNoTracking()
            .Where(s => s.LastEventAt >= lastEventAfter)
            .OrderByDescending(s => s.LastEventAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Inserts new chats and updates known ones. A chat that was not restored at startup comes back as a fresh snapshot
    /// with a new start and no title, so an update keeps the earlier start, a stored rename the record does not carry, and
    /// the stored title when the record has none.
    /// </summary>
    public async Task UpsertAsync(IReadOnlyCollection<SessionRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0)
        {
            return;
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        var ids = records.Select(r => r.Id).ToArray();
        var existing = await db.Sessions.Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        foreach (var record in records)
        {
            if (existing.TryGetValue(record.Id, out var row))
            {
                var stored = (row.Title, row.TitleLocked, row.StartedAt);
                db.Entry(row).CurrentValues.SetValues(record);
                if (stored.TitleLocked && !record.TitleLocked)
                {
                    row.Title = stored.Title;
                    row.TitleLocked = true;
                }
                else if (record.Title is null)
                {
                    row.Title = stored.Title;
                }

                if (stored.StartedAt < record.StartedAt)
                {
                    row.StartedAt = stored.StartedAt;
                }
            }
            else
            {
                db.Sessions.Add(record);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task AppendEventsAsync(IReadOnlyCollection<SessionEventRecord> events, CancellationToken ct = default)
    {
        if (events.Count == 0)
        {
            return;
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        db.SessionEvents.AddRange(events);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SessionEventRecord>> GetEventsAsync(string sessionId, int limit, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.SessionEvents.AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderByDescending(e => e.At)
            .ThenByDescending(e => e.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<int> PruneEventsAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.SessionEvents.Where(e => e.At < olderThan).ExecuteDeleteAsync(ct);
    }
}
