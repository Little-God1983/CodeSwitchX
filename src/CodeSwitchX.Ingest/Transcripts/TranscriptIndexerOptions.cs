namespace CodeSwitchX.Ingest.Transcripts;

public sealed class TranscriptIndexerOptions
{
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often the folder watcher is replaced by a fresh one, followed by a full scan. A watcher can stop without an error,
    /// for example when its folder is renamed away and a new one is created in its place.
    /// </summary>
    public TimeSpan WatcherRefreshInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>A transcript written within this window counts as Working when hooks are absent.</summary>
    public TimeSpan WorkingWindow { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Transcripts last written longer ago than this are indexed for usage only and never create chat rows.</summary>
    public TimeSpan HistoryWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Upper bound of transcript bytes read per file per pass; larger files continue on the next tick.</summary>
    public int MaxBytesPerPass { get; set; } = TranscriptTailer.DefaultMaxBytes;
    /// <summary>Assistant message ids remembered across all transcripts, so a resumed conversation's replayed messages are not counted twice.</summary>
    public int MessageIdMemory { get; set; } = 20_000;
}
