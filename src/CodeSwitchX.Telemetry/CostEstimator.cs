using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Telemetry;

public static class CostEstimator
{
    private const decimal Million = 1_000_000m;

    /// <summary>Anthropic's 1-hour cache write multiplier, for a rule that carries no rate of its own.</summary>
    public const decimal OneHourCacheWriteMultiplier = 2m;

    public static decimal Estimate(TokenUsage usage, PricingRule rule) =>
        (usage.Input * rule.InputPerM
         + usage.Output * rule.OutputPerM
         + usage.CacheWrite * rule.CacheWritePerM
         + usage.CacheWrite1h * OneHourCacheWriteRate(rule)
         + usage.CacheRead * rule.CacheReadPerM) / Million;

    public static decimal OneHourCacheWriteRate(PricingRule rule) => rule.CacheWrite1hPerM ?? rule.InputPerM * OneHourCacheWriteMultiplier;
}
