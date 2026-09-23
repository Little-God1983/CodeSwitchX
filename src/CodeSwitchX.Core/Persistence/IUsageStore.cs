namespace CodeSwitchX.Core.Persistence;

public interface IUsageStore
{
    /// <summary>Adds each delta to the bucket with the same (SessionId, Model, MinuteUtc), creating it when missing.</summary>
    Task AddUsageAsync(IReadOnlyCollection<UsageBucket> deltas, CancellationToken ct = default);
    Task<IReadOnlyList<UsageBucket>> GetBucketsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default);
    Task<IReadOnlyList<TranscriptCursor>> GetCursorsAsync(CancellationToken ct = default);
    Task UpsertCursorsAsync(IReadOnlyCollection<TranscriptCursor> cursors, CancellationToken ct = default);
}
