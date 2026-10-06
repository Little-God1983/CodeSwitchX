using System.Windows;
using System.Windows.Controls;
using CodeSwitchX.UI.Infrastructure;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Infrastructure;

/// <summary>#168: on a narrow window the bottom bar folds its items step by step, only as far as it must, and never overlaps.</summary>
public sealed class GiveWayBarTests
{
    /// <summary>The row is 700 wide with nothing folded, 600 with step 1 folded, 450 with two, 350 with three.</summary>
    private static readonly double[] Widths = [700, 600, 450, 350];

    [Theory]
    [InlineData(800.0, 0)]
    [InlineData(700.0, 0)] // just fits
    [InlineData(699.0, 1)]
    [InlineData(600.0, 1)]
    [InlineData(599.0, 2)]
    [InlineData(450.0, 2)]
    [InlineData(449.0, 3)]
    [InlineData(100.0, 3)] // even all folded it is too wide: all of them fold
    public void It_folds_the_fewest_steps_that_fit(double width, int folded) =>
        GiveWayBar.StepsFor(width, 3, step => Widths[step]).ShouldBe(folded);

    [Fact]
    public void Unlimited_in_width_it_folds_nothing() =>
        GiveWayBar.StepsFor(double.PositiveInfinity, 3, step => Widths[step]).ShouldBe(0);

    [Fact]
    public void With_nothing_marked_there_is_nothing_to_fold() => GiveWayBar.StepsFor(10, 0, _ => 700).ShouldBe(0);

    /// <summary>
    /// Shaped like the bottom bar: a name inside a link on the right (step 1), the sparkline (step 2) and the cost (step 3)
    /// on the left; 640 wide in all, 80 less with step 1, 150 less again with step 2, 100 less again with step 3.
    /// </summary>
    [Theory]
    [InlineData(640.0, 0)]
    [InlineData(639.0, 1)]
    [InlineData(560.0, 1)]
    [InlineData(559.0, 2)]
    [InlineData(410.0, 2)]
    [InlineData(409.0, 3)]
    public Task A_bar_folds_its_items_in_order_as_far_as_its_width_needs(double width, int folded) => StaThread.RunAsync(() =>
    {
        var bar = Bar(out var name, out var sparkline, out var cost);

        Lay(bar, width);

        bar.Folded.ShouldBe(folded);
        name.Visibility.ShouldBe(folded >= 1 ? Visibility.Collapsed : Visibility.Visible);
        sparkline.Visibility.ShouldBe(folded >= 2 ? Visibility.Collapsed : Visibility.Visible);
        cost.Visibility.ShouldBe(folded >= 3 ? Visibility.Collapsed : Visibility.Visible);
        bar.DesiredSize.Width.ShouldBeLessThanOrEqualTo(width, "nothing runs past the bar's width");
    });

    /// <summary>
    /// Narrowed and widened again, as a window is dragged: each width folds exactly what it needs, the name nested in its
    /// link included, whose parents had measured it before.
    /// </summary>
    [Fact]
    public Task Dragged_narrower_and_wider_it_folds_and_unfolds_each_time() => StaThread.RunAsync(() =>
    {
        var bar = Bar(out var name, out _, out _);

        foreach (var (width, folded) in new[] { (900.0, 0), (600.0, 1), (500.0, 2), (300.0, 3), (600.0, 1), (900.0, 0), (450.0, 2) })
        {
            Lay(bar, width);
            bar.Folded.ShouldBe(folded, $"at {width}");
            bar.DesiredSize.Width.ShouldBeLessThanOrEqualTo(width, $"at {width}");
        }

        name.Visibility.ShouldBe(Visibility.Collapsed);
    });

    /// <summary>An item that grows (the cost passes another digit) folds the next step at the same width.</summary>
    [Fact]
    public Task An_item_that_grows_folds_the_next_step() => StaThread.RunAsync(() =>
    {
        var bar = Bar(out _, out _, out _, out var tokens);
        Lay(bar, 600);
        bar.Folded.ShouldBe(1);

        tokens.Width = 160; // 60 wider: the row with step 1 folded is now 620
        bar.UpdateLayout();

        bar.Folded.ShouldBe(2);
    });

    private static GiveWayBar Bar(out FrameworkElement name, out FrameworkElement sparkline, out FrameworkElement cost) =>
        Bar(out name, out sparkline, out cost, out _);

    private static GiveWayBar Bar(out FrameworkElement name, out FrameworkElement sparkline, out FrameworkElement cost, out FrameworkElement tokens)
    {
        name = new Border { Width = 80 };
        GiveWayBar.SetStep(name, 1);
        var link = new Button
        {
            Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) },
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { name, new Border { Width = 50 } } },
        };
        DockPanel.SetDock(link, Dock.Right);

        tokens = new Border { Width = 100 };
        sparkline = new Border { Width = 150 };
        GiveWayBar.SetStep(sparkline, 2);
        cost = new Border { Width = 100 };
        GiveWayBar.SetStep(cost, 3);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { tokens, cost, sparkline, new Border { Width = 160 } } };

        return new GiveWayBar { Child = new DockPanel { Children = { link, row } } };
    }

    private static void Lay(GiveWayBar bar, double width)
    {
        bar.Measure(new Size(width, 20));
        bar.Arrange(new Rect(0, 0, double.IsFinite(width) ? width : bar.DesiredSize.Width, 20));
    }
}
