using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>
/// A row that gives way on a narrow window instead of running its items into each other (#168): the
/// <see cref="GiveWay"/> items inside it fold in the order of their steps, step 1 first, each step only when the row would
/// not fit the width without it. A wide row folds nothing. Folded items still measure their content, so each measure
/// knows the row's width at every step without showing or hiding anything to find out: what folds comes from the width
/// the bar is given and the items' widths now, never from an earlier fold. The items sit side by side in the row, so a
/// step folded takes exactly its items' widths off it.
/// </summary>
public sealed class GiveWayBar : Decorator
{
    /// <summary>Within this much a row still fits: layout rounding (125 % scaling) can leave a sum a hair over the width.</summary>
    private const double Slack = 0.01;

    private readonly List<GiveWay> _items = [];

    /// <summary>Each step's width at the last measure, by step: kept to measure without allocating.</summary>
    private readonly double[] _steps = new double[GiveWay.MaxStep];

    /// <summary>The items in the row at the last measure, the ones it folds.</summary>
    private readonly List<GiveWay> _inRow = [];

    /// <summary>Where an item that joined the bar is now.</summary>
    private enum Place
    {
        /// <summary>In the row, shown or folded.</summary>
        Row,

        /// <summary>Under a collapsed panel (the bar collapsed to its arrow): not laid out, so its width is not current.</summary>
        Hidden,

        /// <summary>Taken out of the bar, with a row or panel it was in.</summary>
        Gone,
    }

    /// <summary>How many steps the last measure folded: 0 none.</summary>
    public int Folded { get; private set; }

    /// <summary>Whether a row this wide fits the width, give or take layout rounding.</summary>
    internal static bool Fits(double row, double width) => row <= width + Slack;

    /// <summary>
    /// The fewest steps to fold for the row to fit <paramref name="width"/>, given its width with nothing folded and each
    /// step's width (<paramref name="steps"/>[0] is step 1); all of them when even that is too wide.
    /// </summary>
    internal static int StepsFor(double width, double unfolded, IReadOnlyList<double> steps)
    {
        var row = unfolded;
        for (var folded = 0; folded < steps.Count; folded++)
        {
            if (Fits(row, width))
            {
                return folded;
            }

            row -= steps[folded];
        }

        return steps.Count;
    }

    /// <summary>The nearest bar above <paramref name="node"/>, if any.</summary>
    internal static GiveWayBar? Above(DependencyObject node)
    {
        for (var up = ParentOf(node); up is not null; up = ParentOf(up))
        {
            if (up is GiveWayBar bar)
            {
                return bar;
            }
        }

        return null;
    }

    /// <summary>The visual parent, or the logical one where the visual tree is not made yet (a button's content before its template).</summary>
    internal static DependencyObject? ParentOf(DependencyObject node) =>
        (node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : null) ?? LogicalTreeHelper.GetParent(node);

    private Place Where(GiveWay item)
    {
        var hidden = false;
        for (DependencyObject? node = item; node is not null; node = ParentOf(node))
        {
            if (node == this)
            {
                return hidden ? Place.Hidden : Place.Row;
            }

            hidden |= node is UIElement { Visibility: Visibility.Collapsed };
        }

        return Place.Gone;
    }

    internal void Join(GiveWay item)
    {
        _items.Add(item);
        InvalidateMeasure();
    }

    internal void Leave(GiveWay item)
    {
        if (_items.Remove(item))
        {
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is not { } child)
        {
            return default;
        }

        // Measured unlimited in width, the row's desired width is never cut to the constraint, so an item that grows later
        // still reaches this measure. Its items join the bar as they measure.
        var unlimited = new Size(double.PositiveInfinity, constraint.Height);
        child.Measure(unlimited);

        Array.Clear(_steps);
        _inRow.Clear();
        var last = 0;
        var unfolded = child.DesiredSize.Width;
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            switch (Where(item))
            {
                case Place.Gone:
                    _items.RemoveAt(i);
                    item.LetGo();
                    continue;
                case Place.Hidden:
                    continue; // under a collapsed panel it is not in the row: neither is its width, and its fold waits
            }

            _inRow.Add(item);

            if (item.Step > 0)
            {
                _steps[item.Step - 1] += item.Natural;
                last = Math.Max(last, item.Step);
            }

            if (item.IsFolded)
            {
                unfolded += item.Natural;
            }
        }

        Folded = StepsFor(constraint.Width, unfolded, new ArraySegment<double>(_steps, 0, last));
        var changed = false;
        foreach (var item in _inRow)
        {
            var fold = item.Step > 0 && item.Step <= Folded;
            changed |= fold != item.IsFolded;
            item.IsFolded = fold;
        }

        if (changed)
        {
            child.Measure(unlimited);
        }

        if (!Fits(child.DesiredSize.Width, constraint.Width))
        {
            // Too wide even folded as far as it goes: measured at the bar's own width the row keeps its docked items (the
            // models' dots, the collapse button) in view and cuts the end of its fill instead. Nothing is left to fold, and
            // a row that shrinks back reports a new size from here too.
            child.Measure(constraint);
        }

        return new Size(Math.Min(child.DesiredSize.Width, constraint.Width), child.DesiredSize.Height);
    }
}
