using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CodeSwitchX.UI.Infrastructure;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Infrastructure;

/// <summary>#168: on a narrow window the bottom bar folds its items step by step, only as far as it must, and never overlaps.</summary>
public sealed class GiveWayBarTests
{
    [Theory]
    [InlineData(700.0, 700.0, true)]
    [InlineData(700.0000001, 700.0, true)] // layout rounding a hair over: it still fits
    [InlineData(700.5, 700.0, false)]
    [InlineData(10_000.0, double.PositiveInfinity, true)]
    public void A_row_fits_its_width_give_or_take_rounding(double row, double width, bool fits) => GiveWayBar.Fits(row, width).ShouldBe(fits);

    [Theory]
    [InlineData(800.0, 0)]
    [InlineData(640.0, 0)] // just fits
    [InlineData(639.0, 1)]
    [InlineData(560.0, 1)]
    [InlineData(559.0, 2)]
    [InlineData(410.0, 2)]
    [InlineData(409.0, 3)]
    [InlineData(100.0, 3)] // even all folded it is too wide: all of them fold
    [InlineData(double.PositiveInfinity, 0)]
    public void It_folds_the_fewest_steps_that_fit(double width, int folded) => GiveWayBar.StepsFor(width, 640, [80, 150, 100]).ShouldBe(folded);

    [Fact]
    public void With_nothing_to_fold_it_folds_nothing() => GiveWayBar.StepsFor(10, 640, []).ShouldBe(0);

    /// <summary>
    /// Shaped like the bottom bar: a name inside a link on the right (step 1), the sparkline (step 2) and the cost (step 3)
    /// on the left; 640 wide in all, 80 less with step 1, 150 less again with step 2, 100 less again with step 3.
    /// </summary>
    [Theory]
    [InlineData(900.0, 0)]
    [InlineData(640.0, 0)]
    [InlineData(639.0, 1)]
    [InlineData(560.0, 1)]
    [InlineData(559.0, 2)]
    [InlineData(410.0, 2)]
    [InlineData(409.0, 3)]
    [InlineData(310.0, 3)]
    public Task A_bar_folds_its_items_in_order_as_far_as_its_width_needs(double width, int folded) => StaThread.RunAsync(() =>
    {
        var bar = Bar(out var parts);

        Lay(bar, width);

        bar.Folded.ShouldBe(folded);
        IsFolded(parts.Name).ShouldBe(folded >= 1);
        IsFolded(parts.Sparkline).ShouldBe(folded >= 2);
        IsFolded(parts.Cost).ShouldBe(folded >= 3);
        Row(bar).ShouldBeLessThanOrEqualTo(width, "the row itself, not only the bar, fits: nothing runs into anything");
    });

    /// <summary>Folded, an item takes no room and shows nothing, and its content's own Visibility is left alone.</summary>
    [Fact]
    public Task A_folded_item_takes_no_room_and_shows_nothing() => StaThread.RunAsync(() =>
    {
        var bar = Bar(out var parts);

        Lay(bar, 300);

        var cost = (GiveWay)parts.Cost.Parent;
        cost.DesiredSize.Width.ShouldBe(0);
        LayoutInformation.GetLayoutClip(cost).ShouldBe(Geometry.Empty);
        parts.Cost.Visibility.ShouldBe(Visibility.Visible);
    });

    /// <summary>
    /// Narrowed and widened again, as a window is dragged: each width folds exactly what it needs, the name nested in its
    /// link included, whose parents had measured it before.
    /// </summary>
    [Fact]
    public Task Dragged_narrower_and_wider_it_folds_and_unfolds_each_time() => StaThread.RunAsync(() =>
    {
        var bar = Bar(out var parts);

        foreach (var (width, folded) in new[] { (900.0, 0), (600.0, 1), (500.0, 2), (320.0, 3), (600.0, 1), (900.0, 0), (450.0, 2), (409.0, 3), (640.0, 0) })
        {
            Lay(bar, width);
            bar.Folded.ShouldBe(folded, $"at {width}");
            Row(bar).ShouldBeLessThanOrEqualTo(width, $"at {width}");
        }

        IsFolded(parts.Name).ShouldBeFalse();
    });

    /// <summary>An item that grows (the cost passes another digit) folds the next step at the same width.</summary>
    [Fact]
    public Task An_item_that_grows_folds_the_next_step() => StaThread.RunAsync(() =>
    {
        var bar = Bar(out var parts);
        Lay(bar, 600);
        bar.Folded.ShouldBe(1);

        parts.Tokens.Width = 160; // 60 wider: the row with step 1 folded is now 620
        bar.UpdateLayout();

        bar.Folded.ShouldBe(2);
    });

    /// <summary>
    /// An item that shrinks while folded (the voice switched off leaves "voice" for "voice · Qwen3-TTS", the cost back to
    /// $0.00 at midnight) comes back at the same width as soon as it fits.
    /// </summary>
    [Fact]
    public Task A_folded_item_that_shrinks_comes_back_once_it_fits() => StaThread.RunAsync(() =>
    {
        var bar = Bar(out var parts);
        Lay(bar, 600);
        bar.Folded.ShouldBe(1);

        parts.Name.Width = 10; // the row unfolded is now 570
        bar.UpdateLayout();

        bar.Folded.ShouldBe(0);
        IsFolded(parts.Name).ShouldBeFalse();
    });

    /// <summary>A folded item that grows while folded stays folded, and the row it would need is judged anew.</summary>
    [Fact]
    public Task A_folded_item_that_grows_while_folded_stays_folded() => StaThread.RunAsync(() =>
    {
        var bar = Bar(out var parts);
        Lay(bar, 500);
        bar.Folded.ShouldBe(2);

        parts.Sparkline.Width = 400;
        bar.UpdateLayout();
        Lay(bar, 640);

        bar.Folded.ShouldBe(2, "shown, the sparkline would need 890");
        Lay(bar, 890);
        bar.Folded.ShouldBe(0);
    });

    /// <summary>A step changed after the bar was laid out counts at once.</summary>
    [Fact]
    public Task A_step_changed_later_counts() => StaThread.RunAsync(() =>
    {
        var bar = Bar(out var parts);
        Lay(bar, 409);
        IsFolded(parts.Cost).ShouldBeTrue();

        ((GiveWay)parts.Cost.Parent).Step = 0;
        bar.UpdateLayout();

        IsFolded(parts.Cost).ShouldBeFalse("it no longer folds");
        IsFolded(parts.Sparkline).ShouldBeTrue();
    });

    /// <summary>An item put into the row later folds with it; one taken out is shown again and no longer the bar's.</summary>
    [Fact]
    public Task Items_added_or_taken_out_later_count() => StaThread.RunAsync(() =>
    {
        var bar = Bar(out var parts);
        Lay(bar, 600);
        var row = (StackPanel)parts.Tokens.Parent;

        var late = new GiveWay { Step = 1, Child = new Border { Width = 50 } };
        row.Children.Add(late);
        bar.UpdateLayout();
        late.IsFolded.ShouldBeTrue("the row is 610 with step 1 folded and it shown");

        var cost = (GiveWay)parts.Cost.Parent;
        Lay(bar, 300);
        cost.IsFolded.ShouldBeTrue();
        row.Children.Remove(cost);
        bar.UpdateLayout();
        cost.IsFolded.ShouldBeFalse("taken out, it is no longer folded by the bar");
    });

    /// <summary>
    /// Too wide even with every step folded, the row cuts the end of its fill and keeps its docked items (the models' dots,
    /// the collapse button) inside the bar.
    /// </summary>
    [Fact]
    public Task Too_wide_even_folded_the_docked_items_stay_in_view() => StaThread.RunAsync(() =>
    {
        var bar = Bar(out var parts);

        Lay(bar, 300); // folded as far as it goes the row needs 310

        bar.Folded.ShouldBe(3);
        var link = (FrameworkElement)((FrameworkElement)parts.Name.Parent).Parent;
        while (link is not Button)
        {
            link = (FrameworkElement)(link.Parent ?? System.Windows.Media.VisualTreeHelper.GetParent(link));
        }

        LayoutInformation.GetLayoutSlot(link).Right.ShouldBeLessThanOrEqualTo(300);
    });

    /// <summary>A folded item taken out and put into another panel shows there: its size and clip are its own again.</summary>
    [Fact]
    public Task A_folded_item_put_elsewhere_shows_there() => StaThread.RunAsync(() =>
    {
        var bar = Bar(out var parts);
        Lay(bar, 300);
        var cost = (GiveWay)parts.Cost.Parent;
        cost.IsFolded.ShouldBeTrue();

        ((StackPanel)parts.Tokens.Parent).Children.Remove(cost);
        var elsewhere = new StackPanel { Orientation = Orientation.Horizontal, Children = { cost } };
        elsewhere.Measure(new Size(300, 20));
        elsewhere.Arrange(new Rect(0, 0, 300, 20));

        cost.DesiredSize.Width.ShouldBe(100);
        LayoutInformation.GetLayoutClip(cost).ShouldNotBe(Geometry.Empty);
    });

    /// <summary>
    /// Items under a collapsed panel (the bar collapsed to its arrow) are not in the row: their old widths do not count, and
    /// they fold as the row needs once shown again.
    /// </summary>
    [Fact]
    public Task Items_under_a_collapsed_panel_do_not_count() => StaThread.RunAsync(() =>
    {
        var bar = Bar(out var parts);
        var row = (StackPanel)parts.Tokens.Parent;
        Lay(bar, 600);
        bar.Folded.ShouldBe(1);

        row.Visibility = Visibility.Collapsed; // the row is 130 without it
        parts.Sparkline.Width = 2000; // a width it no longer has in the row
        bar.UpdateLayout();
        Lay(bar, 200);
        bar.Folded.ShouldBe(0, "only the link is in the row");

        row.Visibility = Visibility.Visible;
        bar.UpdateLayout();
        bar.Folded.ShouldBe(3);
    });

    [Theory]
    [InlineData(-1)]
    [InlineData(GiveWay.MaxStep + 1)]
    public void A_step_out_of_range_is_refused(int step) => Should.Throw<ArgumentException>(() => StaRun(() => new GiveWay { Step = step }));

    /// <summary>Folded, its content is out of the automation tree: a screen reader does not read what is not shown.</summary>
    [Fact]
    public Task Folded_its_content_is_not_read_out() => StaThread.RunAsync(() =>
    {
        var bar = Bar(out var parts);
        var cost = (GiveWay)parts.Cost.Parent;
        cost.Child = new TextBlock { Text = "cost today" }; // a TextBlock has a peer of its own, a Border none
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(cost);

        Lay(bar, 900);
        peer.ResetChildrenCache();
        (peer.GetChildren()?.Count ?? 0).ShouldBe(1);

        Lay(bar, 300);
        peer.ResetChildrenCache();
        (peer.GetChildren()?.Count ?? 0).ShouldBe(0);
    });

    private static void StaRun(Action body)
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (caught is not null)
        {
            throw caught;
        }
    }

    private sealed record Parts(FrameworkElement Name, FrameworkElement Sparkline, FrameworkElement Cost, FrameworkElement Tokens);

    private static GiveWayBar Bar(out Parts parts)
    {
        var name = new Border { Width = 80 };
        var link = new Button
        {
            Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) },
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new GiveWay { Step = 1, Child = name }, new Border { Width = 50 } } },
        };
        DockPanel.SetDock(link, Dock.Right);

        var tokens = new Border { Width = 100 };
        var sparkline = new Border { Width = 150 };
        var cost = new Border { Width = 100 };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { tokens, new GiveWay { Step = 3, Child = cost }, new GiveWay { Step = 2, Child = sparkline }, new Border { Width = 160 } },
        };

        parts = new Parts(name, sparkline, cost, tokens);
        return new GiveWayBar { Child = new DockPanel { Children = { link, row } } };
    }

    private static bool IsFolded(FrameworkElement content) => ((GiveWay)content.Parent).IsFolded;

    /// <summary>The row's own width: the bar reports at most its constraint, so only the row shows an overlap.</summary>
    private static double Row(GiveWayBar bar) => bar.Child.DesiredSize.Width;

    private static void Lay(GiveWayBar bar, double width)
    {
        bar.Measure(new Size(width, 20));
        bar.Arrange(new Rect(0, 0, double.IsFinite(width) ? width : bar.DesiredSize.Width, 20));
    }
}
