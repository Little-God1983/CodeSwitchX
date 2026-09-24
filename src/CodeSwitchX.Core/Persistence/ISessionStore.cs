namespace CodeSwitchX.Core.Persistence;

public interface ISessionStore
{
    Task<IReadOnlyList<SessionRecord>> GetActiveSinceAsync(DateTimeOffset lastEventAfter, CancellationToken ct = default);
    Task UpsertAsync(IReadOnlyCollection<SessionRecord> records, CancellationToken ct = default);
    Task AppendEventsAsync(IReadOnlyCollection<SessionEventRecord> events, CancellationToken ct = default);
    Task<IReadOnlyList<SessionEventRecord>> GetEventsAsync(string sessionId, int limit, CancellationToken ct = default);
    Task<int> PruneEventsAsync(DateTimeOffset olderThan, CancellationToken ct = default);
}
