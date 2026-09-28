using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Telemetry;

namespace CodeSwitchX.Telemetry.Tests;

public class PricingTableTests
{
    [Theory]
    [InlineData("claude-sonnet-5", "claude-sonnet-5")]
    [InlineData("claude-sonnet-5-20260115", "claude-sonnet-5")]
    [InlineData("claude-opus-5-5", "claude-opus-5-5")]
    [InlineData("claude-opus-5", "claude-opus-5")]
    [InlineData("claude-opus-5-20260401", "claude-opus-5")]
    [InlineData("claude-sonnet-4-5-20250929", "claude-sonnet-4-5")]
    [InlineData("CLAUDE-HAIKU-4-5-20251001", "claude-haiku-4-5")]
    [InlineData("claude-opus-4-5@20251101", "claude-opus-4-5")]
    [InlineData("claude-opus-4-6[1m]", "claude-opus-4-6")]
    [InlineData("anthropic.claude-sonnet-4-5-20250929-v1:0", "claude-sonnet-4-5")]
    [InlineData("us.anthropic.claude-opus-4-5-20251101-v1:0", "claude-opus-4-5")]
    [InlineData("global.anthropic.claude-haiku-4-5-20251001-v1:0", "claude-haiku-4-5")]
    [InlineData("eu.anthropic.claude-sonnet-5-v1:0", "claude-sonnet-5")]
    public void Find_uses_the_longest_prefix_on_a_segment_boundary(string model, string expectedRule)
    {
        PricingTable.Default.Find(model).Model.ShouldBe(expectedRule);
    }

    [Theory]
    [InlineData("claude-sonnet-4-5", 200_000)]
    [InlineData("claude-sonnet-4-5[1m]", 1_000_000)]
    [InlineData("claude-sonnet-4-5-20250929[1m]", 1_000_000)]
    [InlineData("claude-sonnet-4-5[1M]", 1_000_000)]
    [InlineData("claude-opus-5[1m]", 1_000_000)]
    [InlineData("claude-newer-6", 0)]
    [InlineData("claude-newer-6[1m]", 1_000_000)]
    [InlineData(null, 0)]
    public void The_context_window_is_the_rules_or_a_million_with_the_1m_option(string? model, long expected)
    {
        PricingTable.Default.ContextWindowOf(model).ShouldBe(expected);
    }

    [Theory]
    [InlineData("claude-opus-55")]
    [InlineData("gpt-5")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_models_get_the_zero_cost_fallback(string? model)
    {
        var rule = PricingTable.Default.Find(model);

        rule.ShouldBeSameAs(PricingTable.Fallback);
        rule.InputPerM.ShouldBe(0);
        rule.ContextWindow.ShouldBe(0, "the window of a model without a rule is not known, so no fill is measured against 200K");
    }

    [Fact]
    public void Default_rules_cover_the_current_lineup_with_1M_context()
    {
        var rules = DefaultPricing.Rules.ToDictionary(r => r.Model);

        rules["claude-fable-5-1"].ContextWindow.ShouldBe(1_000_000);
        rules["claude-opus-5"].InputPerM.ShouldBe(5m);
        rules["claude-opus-5"].OutputPerM.ShouldBe(25m);
        rules["claude-sonnet-5"].InputPerM.ShouldBe(2m);
        rules["claude-haiku-4-5"].ContextWindow.ShouldBe(200_000);
        rules.Values.ShouldAllBe(r => r.CacheWritePerM >= r.InputPerM && r.CacheReadPerM < r.InputPerM);
    }

    [Fact]
    public void User_rules_replace_defaults_with_the_same_model()
    {
        var table = new PricingTable([new PricingRule { Model = "claude-sonnet-5", InputPerM = 42, ContextWindow = 500_000 }]);

        table.Find("claude-sonnet-5").InputPerM.ShouldBe(42);
        table.Find("claude-sonnet-5").ContextWindow.ShouldBe(500_000);
    }
}
