using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
        parts.Name.Visibility.ShouldBe(folded >= 1 ? Visibility.Collapsed : Visibility.Visible);
        parts.Sparkline.Visibility.ShouldBe(folded >= 2 ? Visibility.Collapsed : Visibility.Visible);
        parts.Cost.Visibility.ShouldBe(folded >= 3 ? Visibility.Collapsed : Visibility.Visible);
        Row(bar).ShouldBeLessThanOrEqualTo(width, "the row itself, not only the bar, fits: nothing runs into anything");
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

        parts.Name.Visibility.ShouldBe(Visibility.Visible);
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
    /// A row that keeps its fold changes no element when it measures again (a number on the bar changing): nothing shown
    /// and hidden again on every update.
    /// </summary>
    [Fact]
    public Task Measured_again_at_the_same_fold_it_shows_and_hides_nothing() => StaThread.RunAsync(() =>
    {
        var bar = Bar(out var parts);
        Lay(bar, 450);
        bar.Folded.ShouldBe(2);
        var changes = 0;
        var visibility = DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(UIElement));
        foreach (var element in new[] { parts.Name, parts.Sparkline, parts.Cost })
        {
            visibility.AddValueChanged(element, (_, _) => changes++);
        }

        parts.Tokens.Width = 110;
        bar.UpdateLayout();
        Lay(bar, 455);

        bar.Folded.ShouldBe(2);
        changes.ShouldBe(0);
    });

    /// <summary>
    /// A marked element with a Visibility of its own (the 5-hour bar shows only with a budget) keeps its binding through a
    /// fold: unfolded, it follows the binding again.
    /// </summary>
    [Fact]
    public Task A_folded_element_keeps_its_own_visibility_binding() => StaThread.RunAsync(() =>
    {
        var bar = Bar(out var parts);
        var source = new Shown { Visibility = Visibility.Visible };
        BindingOperations.SetBinding(parts.Cost, UIElement.VisibilityProperty, new Binding(nameof(Shown.Visibility)) { Source = source });

        Lay(bar, 300);
        parts.Cost.Visibility.ShouldBe(Visibility.Collapsed);
        Lay(bar, 900);
        parts.Cost.Visibility.ShouldBe(Visibility.Visible);

        source.Visibility = Visibility.Collapsed;
        parts.Cost.Visibility.ShouldBe(Visibility.Collapsed, "the binding still holds after the fold");
        Lay(bar, 300);
        Lay(bar, 900);
        parts.Cost.Visibility.ShouldBe(Visibility.Collapsed, "unfolding gives it back its own value, not Visible");
    });

    /// <summary>A step set or cleared after the bar was laid out counts at once.</summary>
    [Fact]
    public Task A_step_set_or_cleared_later_counts() => StaThread.RunAsync(() =>
    {
        var bar = Bar(out var parts);
        Lay(bar, 300);
        parts.Cost.Visibility.ShouldBe(Visibility.Collapsed);

        GiveWayBar.SetStep(parts.Cost, 0);
        Lay(bar, 300);
        parts.Cost.Visibility.ShouldBe(Visibility.Visible, "no longer marked, it is shown again");

        GiveWayBar.SetStep(parts.Tokens, 3);
        Lay(bar, 300);
        parts.Tokens.Visibility.ShouldBe(Visibility.Collapsed, "newly marked, it folds");
    });

    private sealed class Shown : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public Visibility Visibility
        {
            get;
            set
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Visibility)));
            }
        }
    }

    private sealed record Parts(FrameworkElement Name, FrameworkElement Sparkline, FrameworkElement Cost, FrameworkElement Tokens);

    private static GiveWayBar Bar(out Parts parts)
    {
        var name = new Border { Width = 80 };
        GiveWayBar.SetStep(name, 1);
        var link = new Button
        {
            Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) },
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { name, new Border { Width = 50 } } },
        };
        DockPanel.SetDock(link, Dock.Right);

        var tokens = new Border { Width = 100 };
        var sparkline = new Border { Width = 150 };
        GiveWayBar.SetStep(sparkline, 2);
        var cost = new Border { Width = 100 };
        GiveWayBar.SetStep(cost, 3);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { tokens, cost, sparkline, new Border { Width = 160 } } };

        parts = new Parts(name, sparkline, cost, tokens);
        return new GiveWayBar { Child = new DockPanel { Children = { link, row } } };
    }

    /// <summary>The row's own width: the bar reports at most its constraint, so only the row shows an overlap.</summary>
    private static double Row(GiveWayBar bar) => bar.Child.DesiredSize.Width;

    private static void Lay(GiveWayBar bar, double width)
    {
        bar.Measure(new Size(width, 20));
        bar.Arrange(new Rect(0, 0, double.IsFinite(width) ? width : bar.DesiredSize.Width, 20));
    }
}
