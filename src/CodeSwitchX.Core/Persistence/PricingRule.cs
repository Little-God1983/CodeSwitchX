namespace CodeSwitchX.Core.Persistence;

/// <summary>Per-model prices in USD per million tokens. <see cref="Model"/> is matched as a prefix of the transcript model id.</summary>
public sealed class PricingRule
{
    public string Model { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public decimal InputPerM { get; set; }
    public decimal OutputPerM { get; set; }
    public decimal CacheWritePerM { get; set; }
    public decimal CacheReadPerM { get; set; }
    public long ContextWindow { get; set; } = 200_000;
}
