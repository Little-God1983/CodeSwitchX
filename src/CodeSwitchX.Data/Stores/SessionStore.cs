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

    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetLastUseByWorkspaceAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var chats = await db.Sessions.AsNoTracking()
            .Where(s => s.WorkspaceId != null && s.LastUseAt != null)
            .Where(SessionRecord.HeldConversation)
            .Select(s => new { WorkspaceId = s.WorkspaceId!.Value, LastUseAt = s.LastUseAt!.Value })
            .ToListAsync(ct);

        return chats.GroupBy(c => c.WorkspaceId).ToDictionary(g => g.Key, g => g.Max(c => c.LastUseAt));
    }

    /// <summary>
    /// Inserts new chats and updates known ones. A chat that was not restored at startup comes back as a fresh snapshot
    /// with a new start, no title and no use, so an update keeps the earlier start, a stored rename the record does not
    /// carry, the stored title when the record has none, and the later of the two last uses.
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
                var stored = (row.Title, row.TitleLocked, row.StartedAt, row.LastUseAt);
                db.Entry(row).CurrentValues.SetValues(record);
                // Nothing unlocks a title (SessionEngine.Rename only locks), so an unlocked record here is a fresh snapshot.
                // A feature that resets a title to automatic has to change this merge as well.
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

                if (stored.LastUseAt is { } storedUse && (record.LastUseAt is not { } use || storedUse > use))
                {
                    row.LastUseAt = storedUse;
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
