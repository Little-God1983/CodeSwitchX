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

    /// <summary>The hook events that are no use of a chat: Claude Code starts and ends one as its window opens, reloads or closes.</summary>
    private static readonly string[] NoUse = ["SessionStart", "SessionEnd"];

    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetLastUseByWorkspaceAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var chats = await db.Sessions.AsNoTracking()
            .Where(s => s.WorkspaceId != null && (s.Title != null || s.LastToolName != null || s.ContextInput != 0 || s.ContextOutput != 0
                || s.ContextCacheWrite != 0 || s.ContextCacheRead != 0 || s.ContextCacheWrite1h != 0))
            .Select(s => new { s.Id, WorkspaceId = s.WorkspaceId!.Value, s.LastEventAt })
            .ToListAsync(ct);

        var used = (await db.SessionEvents.AsNoTracking()
                .Where(e => !NoUse.Contains(e.Kind))
                .GroupBy(e => e.SessionId)
                .Select(g => new { SessionId = g.Key, At = g.Max(e => e.At) })
                .ToListAsync(ct))
            .ToDictionary(e => e.SessionId, e => e.At, StringComparer.Ordinal);
        var told = (await db.SessionEvents.AsNoTracking().Select(e => e.SessionId).Distinct().ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        var lastUse = new Dictionary<Guid, DateTimeOffset>();
        foreach (var chat in chats)
        {
            // Its events tell when it was used; with none kept (older than their retention, or a chat only its transcript
            // told of) its last event does.
            DateTimeOffset? at = used.TryGetValue(chat.Id, out var u) ? u : told.Contains(chat.Id) ? null : chat.LastEventAt;
            if (at is { } use && (!lastUse.TryGetValue(chat.WorkspaceId, out var known) || use > known))
            {
                lastUse[chat.WorkspaceId] = use;
            }
        }

        return lastUse;
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
