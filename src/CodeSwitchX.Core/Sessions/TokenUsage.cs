namespace CodeSwitchX.Core.Sessions;

public readonly record struct TokenUsage(long Input, long Output, long CacheWrite, long CacheRead)
{
    public static TokenUsage Zero => default;

    /// <summary>Tokens occupying the context window on the latest turn: fresh input plus everything read or written to cache.</summary>
    public long ContextTokens => Input + CacheWrite + CacheRead;

    public long Total => Input + Output + CacheWrite + CacheRead;

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) =>
        new(a.Input + b.Input, a.Output + b.Output, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead);
}
