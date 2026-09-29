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
        var usage = new TokenUsage(Input: 1_000_000, Output: 100_000, CacheWrite: 200_000, CacheRead: 2_000_000, CacheWrite1h: 0);

        CostEstimator.Estimate(usage, rule).ShouldBe(3m + 1.5m + 0.75m + 0.60m);
    }

    [Fact]
    public void Zero_usage_costs_nothing()
    {
        CostEstimator.Estimate(TokenUsage.Zero, PricingTable.Default.Find("claude-opus-5")).ShouldBe(0m);
    }

    [Fact]
    public void A_one_hour_cache_write_is_priced_at_its_own_rate()
    {
        var rule = new PricingRule { Model = "m", InputPerM = 3m, OutputPerM = 15m, CacheWritePerM = 3.75m, CacheWrite1hPerM = 6m, CacheReadPerM = 0.30m };
        var usage = new TokenUsage(Input: 0, Output: 0, CacheWrite: 200_000, CacheRead: 0, CacheWrite1h: 500_000);

        CostEstimator.Estimate(usage, rule).ShouldBe(0.75m + 3m);
    }

    [Fact]
    public void A_rule_without_a_one_hour_rate_prices_such_writes_at_twice_its_input()
    {
        var rule = new PricingRule { Model = "m", InputPerM = 3m, OutputPerM = 15m, CacheWritePerM = 3.75m, CacheReadPerM = 0.30m };
        var usage = new TokenUsage(Input: 0, Output: 0, CacheWrite: 0, CacheRead: 0, CacheWrite1h: 1_000_000);

        CostEstimator.Estimate(usage, rule).ShouldBe(6m, "a rule of the user's own that never set the rate must not make these writes free");
    }
}
