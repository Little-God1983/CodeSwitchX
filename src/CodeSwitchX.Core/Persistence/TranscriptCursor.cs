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

    /// <summary>
    /// Claude Code's own part of the transcript's context (system prompt, tools, memory files), which a compact keeps: the
    /// smallest reply as far as it is read, or the reply after a compact minus what the compact kept, which leaves out the
    /// first prompt (0 before either). A restart must not lose it. -1 marks a cursor stored before it was, whose replies
    /// behind the offset were not looked at.
    /// </summary>
    public long ContextBase { get; set; }

    /// <summary>
    /// What the latest compact kept while the reply after it is not read yet (null otherwise): that reply measures Claude
    /// Code's own part, also when the app was closed in between (Raven compacts idle chats).
    /// </summary>
    public long? CompactKept { get; set; }
}
