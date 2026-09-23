namespace CodeSwitchX.Ingest.Transcripts;

public sealed class TranscriptIndexerOptions
{
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>A transcript written within this window counts as Working when hooks are absent.</summary>
    public TimeSpan WorkingWindow { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Transcripts last written longer ago than this are indexed for usage only and never create chat rows.</summary>
    public TimeSpan HistoryWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Upper bound of transcript bytes read per file per pass; larger files continue on the next tick.</summary>
    public int MaxBytesPerPass { get; set; } = TranscriptTailer.DefaultMaxBytes;
    public int MessageIdMemory { get; set; } = 512;
}
