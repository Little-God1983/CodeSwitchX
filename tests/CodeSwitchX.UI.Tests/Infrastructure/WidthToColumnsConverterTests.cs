using System.Globalization;
using CodeSwitchX.UI.Infrastructure;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Infrastructure;

/// <summary>#155: the engine cards go under each other on a narrow page instead of being cut.</summary>
public sealed class WidthToColumnsConverterTests
{
    [Theory]
    [InlineData(700.0, 3)]
    [InlineData(630.0, 3)]
    [InlineData(629.0, 2)]
    [InlineData(420.0, 2)]
    [InlineData(300.0, 1)]
    [InlineData(40.0, 1)]
    [InlineData(5000.0, 3)]
    public void As_many_columns_as_fit_one_to_three(double width, int columns) =>
        new WidthToColumnsConverter().Convert(width, typeof(int), "210,3", CultureInfo.InvariantCulture).ShouldBe(columns);

    /// <summary>The width each card gets: its share of the row, a hair under so the last one of a row stays on it.</summary>
    [Theory]
    [InlineData(700.0, 232.0)]
    [InlineData(500.0, 249.0)]
    [InlineData(300.0, 299.0)]
    public void For_a_width_target_each_column_s_width(double width, double each) =>
        new WidthToColumnsConverter().Convert(width, typeof(double), "210,3", CultureInfo.InvariantCulture).ShouldBe(each);

    [Fact]
    public void Before_it_is_measured_all_columns_show() =>
        new WidthToColumnsConverter().Convert(0.0, typeof(int), "210,3", CultureInfo.InvariantCulture).ShouldBe(3);
}
