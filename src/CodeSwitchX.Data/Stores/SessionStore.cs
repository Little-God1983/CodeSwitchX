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
    /// The hook events that use a chat, for a row stored before <see cref="SessionRecord.LastUseAt"/> (#278): the ones that
    /// make it work or ask. Not its start and end (its window opened, reloaded or closed), nor a turn's end or the idle
    /// notice a minute after it.
    /// </summary>
    private static readonly string[] Use = ["UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest"];

    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetLastUseByWorkspaceAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var chats = await db.Sessions.AsNoTracking()
            .Where(s => s.WorkspaceId != null)
            .Where(SessionRecord.HeldConversation)
            .Select(s => new { s.Id, WorkspaceId = s.WorkspaceId!.Value, s.LastEventAt, s.LastUseAt })
            .ToListAsync(ct);

        // Only the rows stored before #278 that were not used since go by their events.
        var unknown = chats.Where(c => c.LastUseAt is null).Select(c => c.Id).ToArray();
        var used = (await db.SessionEvents.AsNoTracking()
                .Where(e => Use.Contains(e.Kind) && unknown.Contains(e.SessionId))
                .GroupBy(e => e.SessionId)
                .Select(g => new { SessionId = g.Key, At = g.Max(e => e.At) })
                .ToListAsync(ct))
            .ToDictionary(e => e.SessionId, e => e.At, StringComparer.Ordinal);
        var told = (await db.SessionEvents.AsNoTracking().Where(e => unknown.Contains(e.SessionId)).Select(e => e.SessionId).Distinct().ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var lastUse = new Dictionary<Guid, DateTimeOffset>();
        foreach (var chat in chats)
        {
            // Its stored use, the one the engine keeps live. A row from before has its events tell when it was used; with
            // none kept (older than their retention, or a chat only its transcript told of) its last event does.
            DateTimeOffset? at = chat.LastUseAt ?? (used.TryGetValue(chat.Id, out var u) ? u : told.Contains(chat.Id) ? null : chat.LastEventAt);
            if (at is { } use && (!lastUse.TryGetValue(chat.WorkspaceId, out var known) || use > known))
            {
                lastUse[chat.WorkspaceId] = use;
            }
        }

        return lastUse;
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
