namespace CodeSwitchX.Core.Persistence;

public sealed class TranscriptCursor
{
    /// <summary>Normalised transcript path (primary key).</summary>
    public string Path { get; set; } = string.Empty;
    public long ByteOffset { get; set; }
    public DateTimeOffset LastWriteUtc { get; set; }
    public string? SessionId { get; set; }

    /// <summary>
    /// The chat's title as far as the transcript is read, and where it came from, so a restart does not lose a title the
    /// chat engine has not shown yet and knows which later line may replace it.
    /// </summary>
    public string? Title { get; set; }
    public TitleSource TitleSource { get; set; }
}
