using System.Windows;
using System.Windows.Controls;
using CodeSwitchX.UI.Infrastructure;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Infrastructure;

/// <summary>#155: the engine cards go three, two or one to a row as the page has room, never cut.</summary>
public sealed class CardColumnsPanelTests
{
    [Theory]
    [InlineData(700.0, 3)]
    [InlineData(650.0, 3)]
    [InlineData(649.0, 2)]
    [InlineData(430.0, 2)]
    [InlineData(429.0, 1)]
    [InlineData(40.0, 1)]
    [InlineData(5000.0, 3)]
    [InlineData(double.PositiveInfinity, 3)]
    public void As_many_columns_as_fit_one_to_three(double width, int columns) =>
        CardColumnsPanel.ColumnsFor(width, 210, 3, 10).ShouldBe(columns);

    /// <summary>Two to a row at 500: equal columns with the gap between, each row as tall as its own tallest card.</summary>
    [Fact]
    public Task Cards_are_laid_out_in_rows_each_as_tall_as_its_tallest_card() => StaThread.RunAsync(() =>
    {
        var panel = new CardColumnsPanel { MinColumnWidth = 210, MaxColumns = 3, Gap = 10 };
        double[] heights = [100, 300, 50];
        foreach (var height in heights)
        {
            panel.Children.Add(new Border { Height = height });
        }

        panel.Measure(new Size(500, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 500, panel.DesiredSize.Height));

        panel.DesiredSize.Height.ShouldBe(300 + 10 + 50, "the first row is as tall as its taller card, the second as its own");
        Slot(panel, 0).ShouldBe(new Rect(0, 0, 245, 300));
        Slot(panel, 1).ShouldBe(new Rect(255, 0, 245, 300));
        Slot(panel, 2).ShouldBe(new Rect(0, 310, 245, 50));
    });

    private static Rect Slot(Panel panel, int index) => System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot((FrameworkElement)panel.Children[index]);
}
