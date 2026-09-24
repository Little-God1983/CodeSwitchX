using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Telemetry;

namespace CodeSwitchX.Telemetry.Tests;

public class CostEstimatorTests
{
    [Fact]
    public void Cost_is_tokens_times_rate_per_million()
    {
        var rule = new PricingRule { Model = "m", InputPerM = 3m, OutputPerM = 15m, CacheWritePerM = 3.75m, CacheReadPerM = 0.30m };
        var usage = new TokenUsage(Input: 1_000_000, Output: 100_000, CacheWrite: 200_000, CacheRead: 2_000_000);

        CostEstimator.Estimate(usage, rule).ShouldBe(3m + 1.5m + 0.75m + 0.60m);
    }

    [Fact]
    public void Zero_usage_costs_nothing()
    {
        CostEstimator.Estimate(TokenUsage.Zero, PricingTable.Default.Find("claude-opus-5")).ShouldBe(0m);
    }
}
