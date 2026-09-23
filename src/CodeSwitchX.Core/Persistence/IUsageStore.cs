namespace CodeSwitchX.Core.Persistence;

public interface IUsageStore
{
    /// <summary>Adds each delta to the bucket with the same (SessionId, Model, MinuteUtc), creating it when missing.</summary>
    Task AddUsageAsync(IReadOnlyCollection<UsageBucket> deltas, CancellationToken ct = default);
    Task<IReadOnlyList<UsageBucket>> GetBucketsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default);
    Task<IReadOnlyList<TranscriptCursor>> GetCursorsAsync(CancellationToken ct = default);
    Task UpsertCursorsAsync(IReadOnlyCollection<TranscriptCursor> cursors, CancellationToken ct = default);

    /// <summary>
    /// Writes usage deltas, transcript cursors and the ids of the assistant messages the usage came from in one
    /// transaction, so a cursor never advances past usage that was not saved and a saved message is never counted again.
    /// </summary>
    Task CommitAsync(IReadOnlyCollection<UsageBucket> deltas, IReadOnlyCollection<TranscriptCursor> cursors, IReadOnlyCollection<string> messageIds,
        CancellationToken ct = default);

    /// <summary>The most recently saved assistant message ids, oldest first, at most <paramref name="limit"/> of them.</summary>
    Task<IReadOnlyList<string>> GetSeenMessageIdsAsync(int limit, CancellationToken ct = default);

    /// <summary>Deletes all but the newest <paramref name="keep"/> saved message ids.</summary>
    Task<int> PruneSeenMessagesAsync(int keep, CancellationToken ct = default);
}
