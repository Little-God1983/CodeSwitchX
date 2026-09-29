namespace CodeSwitchX.Core.Persistence;

/// <summary>Per-model prices in USD per million tokens. <see cref="Model"/> is matched as a prefix of the transcript model id.</summary>
public sealed class PricingRule
{
    public string Model { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public decimal InputPerM { get; set; }
    public decimal OutputPerM { get; set; }
    /// <summary>Writes to the 5-minute cache.</summary>
    public decimal CacheWritePerM { get; set; }
    public decimal CacheReadPerM { get; set; }
    /// <summary>Writes to the 1-hour cache (2x input at Anthropic's list prices, against 1.25x for the 5-minute cache).</summary>
    public decimal CacheWrite1hPerM { get; set; }
    public long ContextWindow { get; set; } = 200_000;
}
