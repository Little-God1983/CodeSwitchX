namespace CodeSwitchX.Core.Sessions;

/// <param name="CacheWrite">Tokens written to the 5-minute cache.</param>
/// <param name="CacheWrite1h">Tokens written to the 1-hour cache, priced at their own rate; a cache write like the others otherwise.</param>
public readonly record struct TokenUsage(long Input, long Output, long CacheWrite, long CacheRead, long CacheWrite1h = 0)
{
    public static TokenUsage Zero => default;

    /// <summary>Tokens occupying the context window on the latest turn: fresh input plus everything read or written to cache.</summary>
    public long ContextTokens => Input + CacheWrite + CacheWrite1h + CacheRead;

    public long Total => Input + Output + CacheWrite + CacheWrite1h + CacheRead;

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) =>
        new(a.Input + b.Input, a.Output + b.Output, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead, a.CacheWrite1h + b.CacheWrite1h);
}
