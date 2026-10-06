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
        // A row taken out with its items in it: they are no longer this bar's.
        foreach (var gone in _items.Where(item => Above(item) != this).ToList())
        {
            _items.Remove(gone);
            gone.LetGo();
        }

        var steps = new double[_items.Count == 0 ? 0 : _items.Max(item => item.Step)];
        var unfolded = child.DesiredSize.Width;
        foreach (var item in _items)
        {
            if (item.Step > 0)
            {
                steps[item.Step - 1] += item.Natural;
            }

            if (item.IsFolded)
            {
                unfolded += item.Natural;
            }
        }

        Folded = StepsFor(constraint.Width, unfolded, steps);
        var changed = false;
        foreach (var item in _items)
        {
            var fold = item.Step > 0 && item.Step <= Folded;
            changed |= fold != item.IsFolded;
            item.IsFolded = fold;
        }

        if (changed)
        {
            child.Measure(unlimited);
        }

        return new Size(Math.Min(child.DesiredSize.Width, constraint.Width), child.DesiredSize.Height);
    }
}
