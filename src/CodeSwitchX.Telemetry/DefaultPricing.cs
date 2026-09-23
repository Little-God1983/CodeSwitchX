using CodeSwitchX.Core.Persistence;

namespace CodeSwitchX.Telemetry;

/// <summary>
/// Shipped defaults in USD per million tokens (Anthropic list prices as of 2026-09; estimates the user can edit).
/// Cache write is 1.25x input and cache read 0.10x input unless Anthropic publishes a different rate.
/// </summary>
public static class DefaultPricing
{
    public static IReadOnlyList<PricingRule> Rules { get; } =
    [
        Rule("claude-fable-5-1", "Claude Fable 5.1", 10m, 50m, 12.5m, 0.25m, 1_000_000),
        Rule("claude-fable-5", "Claude Fable 5", 10m, 50m, 12.5m, 1m, 1_000_000),
        Rule("claude-opus-5-5", "Claude Opus 5.5", 4m, 20m, 5m, 0.20m, 1_000_000),
        Rule("claude-opus-5", "Claude Opus 5", 5m, 25m, 6.25m, 0.50m, 1_000_000),
        Rule("claude-opus-4-8", "Claude Opus 4.8", 5m, 25m, 6.25m, 0.50m, 1_000_000),
        Rule("claude-opus-4-7", "Claude Opus 4.7", 5m, 25m, 6.25m, 0.50m, 1_000_000),
        Rule("claude-opus-4-6", "Claude Opus 4.6", 5m, 25m, 6.25m, 0.50m, 1_000_000),
        Rule("claude-opus-4-5", "Claude Opus 4.5", 5m, 25m, 6.25m, 0.50m, 200_000),
        Rule("claude-opus-4-1", "Claude Opus 4.1", 15m, 75m, 18.75m, 1.50m, 200_000),
        Rule("claude-opus-4", "Claude Opus 4", 15m, 75m, 18.75m, 1.50m, 200_000),
        Rule("claude-sonnet-5", "Claude Sonnet 5", 2m, 10m, 2.5m, 0.20m, 1_000_000),
        Rule("claude-sonnet-4-6", "Claude Sonnet 4.6", 3m, 15m, 3.75m, 0.30m, 1_000_000),
        Rule("claude-sonnet-4-5", "Claude Sonnet 4.5", 3m, 15m, 3.75m, 0.30m, 200_000),
        Rule("claude-sonnet-4", "Claude Sonnet 4", 3m, 15m, 3.75m, 0.30m, 200_000),
        Rule("claude-haiku-4-5", "Claude Haiku 4.5", 1m, 5m, 1.25m, 0.10m, 200_000),
        Rule("claude-3-5-haiku", "Claude Haiku 3.5", 0.8m, 4m, 1m, 0.08m, 200_000),
    ];

    private static PricingRule Rule(string model, string display, decimal input, decimal output, decimal cacheWrite, decimal cacheRead, long context) => new()
    {
        Model = model,
        DisplayName = display,
        InputPerM = input,
        OutputPerM = output,
        CacheWritePerM = cacheWrite,
        CacheReadPerM = cacheRead,
        ContextWindow = context,
    };
}
