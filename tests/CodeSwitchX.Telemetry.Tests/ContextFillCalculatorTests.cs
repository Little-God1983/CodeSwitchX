using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Telemetry;

namespace CodeSwitchX.Telemetry.Tests;

public class ContextFillCalculatorTests
{
    private static readonly PricingRule Rule = new() { Model = "m", ContextWindow = 200_000 };

    [Fact]
    public void Fill_is_context_tokens_over_the_window()
    {
        ContextFillCalculator.Fill(new TokenUsage(10_000, 5_000, 20_000, 70_000), Rule).ShouldBe(0.5, tolerance: 1e-9);
    }

    [Fact]
    public void Fill_is_clamped_to_one_and_safe_for_a_zero_window()
    {
        ContextFillCalculator.Fill(new TokenUsage(400_000, 0, 0, 0), Rule).ShouldBe(1.0);
        ContextFillCalculator.Fill(new TokenUsage(1, 0, 0, 0), new PricingRule { Model = "m", ContextWindow = 0 }).ShouldBe(0.0);
    }

    [Theory]
    [InlineData(0.0, ContextPressure.Normal)]
    [InlineData(0.79, ContextPressure.Normal)]
    [InlineData(0.80, ContextPressure.Amber)]
    [InlineData(0.89, ContextPressure.Amber)]
    [InlineData(0.90, ContextPressure.Red)]
    [InlineData(1.0, ContextPressure.Red)]
    public void Pressure_thresholds_follow_the_spec(double fill, ContextPressure expected)
    {
        ContextFillCalculator.Level(fill).ShouldBe(expected);
    }
}
