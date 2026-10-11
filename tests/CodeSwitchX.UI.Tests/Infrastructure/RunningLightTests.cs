using CodeSwitchX.UI.Infrastructure;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Infrastructure;

/// <summary>#276: the streak that runs round a working tile's border.</summary>
public sealed class RunningLightTests
{
    [Theory]
    [InlineData(100, 50, 0, 300)]
    [InlineData(298, 148, 9, 2 * 280 + 2 * 130 + 2 * Math.PI * 9)]
    public void The_outline_of_a_rounded_rectangle_is_its_straight_sides_and_a_circle(double width, double height, double radius, double perimeter) =>
        RunningLight.Perimeter(width, height, radius).ShouldBe(perimeter, 1e-9);

    /// <summary>A pattern exactly as long as the outline shows one streak, which the offset takes once round per lap.</summary>
    [Fact]
    public void One_streak_whose_pattern_is_as_long_as_the_outline_and_whose_offset_runs_one_outline_per_lap()
    {
        var (dashes, from, to) = RunningLight.Streak(length: 60, perimeter: 900, thickness: 2);

        dashes.ShouldBe([30, 420]);
        (dashes.Sum() * 2).ShouldBe(900, "dash and gap together, in pixels, are the outline");
        from.ShouldBe(30, "the streak's head starts at the outline's start");
        ((to - from) * 2).ShouldBe(900, "one lap moves the head once round, clockwise as the offset grows");
    }
}
