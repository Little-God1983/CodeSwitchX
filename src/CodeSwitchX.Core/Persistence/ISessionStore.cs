namespace CodeSwitchX.Core.Persistence;

public interface ISessionStore
{
    Task<IReadOnlyList<SessionRecord>> GetActiveSinceAsync(DateTimeOffset lastEventAfter, CancellationToken ct = default);

    /// <summary>
    /// When a chat of each workspace was last used (#276): the latest <see cref="SessionRecord.LastUseAt"/> of its chats
    /// (#278), the same use the Yard sees live. Chats without one and sessions that never held a conversation tell none.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetLastUseByWorkspaceAsync(CancellationToken ct = default);
    Task UpsertAsync(IReadOnlyCollection<SessionRecord> records, CancellationToken ct = default);
    Task AppendEventsAsync(IReadOnlyCollection<SessionEventRecord> events, CancellationToken ct = default);
    Task<IReadOnlyList<SessionEventRecord>> GetEventsAsync(string sessionId, int limit, CancellationToken ct = default);
    Task<int> PruneEventsAsync(DateTimeOffset olderThan, CancellationToken ct = default);
}
