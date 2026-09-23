namespace CodeSwitchX.Ingest.Transcripts;

public sealed class TranscriptIndexerOptions
{
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>A transcript written within this window counts as Working when hooks are absent.</summary>
    public TimeSpan WorkingWindow { get; set; } = TimeSpan.FromSeconds(5);
    public int MessageIdMemory { get; set; } = 512;
}
