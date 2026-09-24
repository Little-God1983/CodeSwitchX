namespace CodeSwitchX.Data;

public sealed class PersistenceWriterOptions
{
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    public int MaxBatch { get; set; } = 500;

    /// <summary>Opt-in: store raw hook payload JSON (contains prompts and file paths).</summary>
    public bool StorePayloads { get; set; }

    public static readonly TimeSpan DefaultEventRetention = TimeSpan.FromDays(7);

    /// <summary>Hook event rows older than this are deleted; they only serve the recent-activity view.</summary>
    public TimeSpan EventRetention { get; set; } = DefaultEventRetention;
    public TimeSpan PruneInterval { get; set; } = TimeSpan.FromHours(1);

    public const int DefaultSeenMessageIdsKept = 20_000;

    /// <summary>Assistant message ids kept for resume deduplication; the oldest beyond this count are pruned.</summary>
    public int SeenMessageIdsKept { get; set; } = DefaultSeenMessageIdsKept;
}
