using CodeSwitchX.Telemetry;

namespace CodeSwitchX.Telemetry.Tests;

public class CacheWarmthCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static TimeSpan LeftAfter(double minutes) => CacheWarmthCalculator.Left(Now.AddMinutes(-minutes), Now);

    [Theory]
    [InlineData(0, CacheWarmth.Hot)]
    [InlineData(39.9, CacheWarmth.Hot)]
    [InlineData(40, CacheWarmth.Warm)]
    [InlineData(59.9, CacheWarmth.Warm)]
    [InlineData(60, CacheWarmth.Cold)]
    [InlineData(600, CacheWarmth.Cold)]
    public void Hot_for_40_minutes_then_warm_for_20_then_cold(double minutesAgo, CacheWarmth expected)
    {
        CacheWarmthCalculator.Level(LeftAfter(minutesAgo)).ShouldBe(expected);
    }

    [Fact]
    public void Left_is_what_remains_of_the_hour()
    {
        LeftAfter(44).ShouldBe(TimeSpan.FromMinutes(16));
    }

    [Fact]
    public void Left_never_leaves_the_hour()
    {
        LeftAfter(90).ShouldBe(TimeSpan.Zero);
        LeftAfter(-5).ShouldBe(CacheWarmthCalculator.Lifetime, "a clock a little off must not fill the gauge past full");
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(30, 0.5)]
    [InlineData(59, 1.0 / 60)]
    public void Fill_drains_over_the_hour(double minutesAgo, double expected)
    {
        CacheWarmthCalculator.Fill(LeftAfter(minutesAgo)).ShouldBe(expected, tolerance: 1e-9);
    }

    [Fact]
    public void A_cold_cache_fills_the_gauge_so_it_does_not_look_like_a_chat_without_data()
    {
        CacheWarmthCalculator.Fill(LeftAfter(60)).ShouldBe(1.0);
    }
}
