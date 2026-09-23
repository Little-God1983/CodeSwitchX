namespace CodeSwitchX.Core.Persistence;

/// <summary>
/// An assistant message whose usage has been counted. <c>claude --resume</c> copies earlier messages, with their
/// usage, into a new transcript file; remembering the ids across restarts keeps them from counting twice.
/// </summary>
public sealed class SeenMessage
{
    /// <summary>Insertion order; the newest ids are the ones worth keeping.</summary>
    public long Seq { get; set; }
    public string MessageId { get; set; } = string.Empty;
}
