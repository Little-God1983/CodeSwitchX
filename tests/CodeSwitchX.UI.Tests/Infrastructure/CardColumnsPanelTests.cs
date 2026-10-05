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
    public void As_many_columns_as_fit_one_to_three(double width, int columns) =>
        CardColumnsPanel.ColumnsFor(width, 210, 3, 10, cards: 3).ShouldBe(columns);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(5, 3)]
    public void Unlimited_in_width_as_many_columns_as_cards_up_to_three(int cards, int columns) =>
        CardColumnsPanel.ColumnsFor(double.PositiveInfinity, 210, 3, 10, cards).ShouldBe(columns);

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

    /// <summary>
    /// Arranged a hair wider than it was measured (layout rounding), it keeps the columns it measured: the cards were sized
    /// for those, and in narrower columns their text would run past the rows' measured height and be cut.
    /// </summary>
    [Fact]
    public Task Arranged_a_hair_wider_it_keeps_the_columns_it_measured() => StaThread.RunAsync(() =>
    {
        var panel = new CardColumnsPanel { MinColumnWidth = 210, MaxColumns = 3, Gap = 10 };
        for (var i = 0; i < 3; i++)
        {
            panel.Children.Add(new Border { Height = 100 });
        }

        panel.Measure(new Size(649.6, double.PositiveInfinity)); // two columns
        panel.Arrange(new Rect(0, 0, 650, panel.DesiredSize.Height));

        Slot(panel, 2).Y.ShouldBe(110, "the third card stays on the second row");
    });

    /// <summary>Arranged a hair narrower than it was measured, the cards keep the width they were measured at: no extra line to cut.</summary>
    [Fact]
    public Task Arranged_a_hair_narrower_the_cards_keep_their_measured_width() => StaThread.RunAsync(() =>
    {
        var panel = new CardColumnsPanel { MinColumnWidth = 210, MaxColumns = 3, Gap = 10 };
        for (var i = 0; i < 2; i++)
        {
            panel.Children.Add(new Border { Height = 100 });
        }

        panel.Measure(new Size(649.4, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 649, panel.DesiredSize.Height));

        Slot(panel, 0).Width.ShouldBe((649.4 - 10) / 2, 0.001);
    });

    /// <summary>Not limited in width, it asks for as many columns as it has cards, up to three, and no more.</summary>
    [Fact]
    public Task Unlimited_in_width_it_asks_only_for_the_columns_its_cards_fill() => StaThread.RunAsync(() =>
    {
        var panel = new CardColumnsPanel { MinColumnWidth = 210, MaxColumns = 3, Gap = 10 };
        panel.Children.Add(new Border { Width = 100, Height = 50 });

        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        panel.DesiredSize.ShouldBe(new Size(100, 50));
    });

    private static Rect Slot(Panel panel, int index) => System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot((FrameworkElement)panel.Children[index]);
}
