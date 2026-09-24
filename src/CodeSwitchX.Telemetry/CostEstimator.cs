using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Telemetry;

public static class CostEstimator
{
    private const decimal Million = 1_000_000m;

    public static decimal Estimate(TokenUsage usage, PricingRule rule) =>
        (usage.Input * rule.InputPerM
         + usage.Output * rule.OutputPerM
         + usage.CacheWrite * rule.CacheWritePerM
         + usage.CacheRead * rule.CacheReadPerM) / Million;
}
