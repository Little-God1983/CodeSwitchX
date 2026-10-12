namespace CodeSwitchX.Core.Persistence;

public interface ISessionStore
{
    Task<IReadOnlyList<SessionRecord>> GetActiveSinceAsync(DateTimeOffset lastEventAfter, CancellationToken ct = default);

    /// <summary>
    /// When a chat of each workspace was last used (#276), by the chats' <see cref="SessionRecord.LastUseAt"/> (#278), the
    /// same use the Yard sees live. A row stored before that goes by the hook events kept: a prompt, a tool, a question;
    /// its start and end (its window opened, reloaded or closed) are no use. One none of whose events are kept (older than
    /// their retention, or one only its transcript told of) goes by its last event. Sessions that never held a
    /// conversation are left out.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetLastUseByWorkspaceAsync(CancellationToken ct = default);
    Task UpsertAsync(IReadOnlyCollection<SessionRecord> records, CancellationToken ct = default);
    Task AppendEventsAsync(IReadOnlyCollection<SessionEventRecord> events, CancellationToken ct = default);
    Task<IReadOnlyList<SessionEventRecord>> GetEventsAsync(string sessionId, int limit, CancellationToken ct = default);
    Task<int> PruneEventsAsync(DateTimeOffset olderThan, CancellationToken ct = default);
}
