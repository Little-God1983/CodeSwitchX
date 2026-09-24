namespace CodeSwitchX.Core.Persistence;

public sealed class TranscriptCursor
{
    /// <summary>Normalised transcript path (primary key).</summary>
    public string Path { get; set; } = string.Empty;
    public long ByteOffset { get; set; }
    public DateTimeOffset LastWriteUtc { get; set; }
    public string? SessionId { get; set; }
}
