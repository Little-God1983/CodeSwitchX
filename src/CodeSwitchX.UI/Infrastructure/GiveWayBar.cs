using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>
/// A row that gives way on a narrow window instead of running its items into each other (#168): the elements inside it
/// marked with a <see cref="StepProperty"/> fold (collapse) in that order, step 1 first, each step only when the row would
/// not fit the width without it. A wide row folds nothing. The row is measured unlimited in width at each step, so what
/// folds comes from the width the bar is given and the items' own widths, never from the row's folded size. A marked
/// element's Visibility is the bar's: do not bind or set it elsewhere.
/// </summary>
public sealed class GiveWayBar : Decorator
{
    public static readonly DependencyProperty StepProperty = DependencyProperty.RegisterAttached("Step", typeof(int), typeof(GiveWayBar),
        new PropertyMetadata(0));

    /// <summary>When the element folds: 1 first, then 2, and so on; 0 (the default) never.</summary>
    public static int GetStep(DependencyObject element) => (int)element.GetValue(StepProperty);

    public static void SetStep(DependencyObject element, int value) => element.SetValue(StepProperty, value);

    /// <summary>How many steps the last measure folded: 0 none.</summary>
    public int Folded { get; private set; }

    /// <summary>
    /// The fewest steps to fold for the row to fit <paramref name="width"/>, given its width with that many folded; all of
    /// them when even that is too wide. An unlimited width folds nothing.
    /// </summary>
    internal static int StepsFor(double width, int steps, Func<int, double> widthWith)
    {
        if (!double.IsFinite(width))
        {
            return 0;
        }

        for (var folded = 0; folded < steps; folded++)
        {
            if (widthWith(folded) <= width)
            {
                return folded;
            }
        }

        return steps;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is not { } child)
        {
            return default;
        }

        var marked = Marked(child);
        var steps = marked.Count == 0 ? 0 : marked.Max(GetStep);
        // Measured unlimited in width, the row's desired width is never cut to the constraint, so an item that grows later
        // still reaches this measure and folds the next step.
        var unlimited = new Size(double.PositiveInfinity, constraint.Height);
        Folded = StepsFor(constraint.Width, steps, folded =>
        {
            Fold(marked, folded);
            child.Measure(unlimited);
            return child.DesiredSize.Width;
        });
        Fold(marked, Folded);
        child.Measure(unlimited);
        return new Size(Math.Min(child.DesiredSize.Width, constraint.Width), child.DesiredSize.Height);
    }

    /// <summary>Folds the marked elements of the first <paramref name="folded"/> steps and shows the rest.</summary>
    private void Fold(List<UIElement> marked, int folded)
    {
        foreach (var element in marked)
        {
            var step = GetStep(element);
            var visibility = step <= folded ? Visibility.Collapsed : Visibility.Visible;
            if (element.Visibility == visibility)
            {
                continue;
            }

            element.Visibility = visibility;
            // A changed element marks only its parent for a new measure: everything up to the bar must measure again, or a
            // parent that was not marked returns its old size.
            for (var up = VisualTreeHelper.GetParent(element) as UIElement; up is not null && up != this; up = VisualTreeHelper.GetParent(up) as UIElement)
            {
                up.InvalidateMeasure();
            }
        }
    }

    private static List<UIElement> Marked(DependencyObject root)
    {
        var marked = new List<UIElement>();
        Collect(root, marked);
        return marked;
    }

    /// <summary>The logical tree, so a button's content counts before its template has made the visual tree.</summary>
    private static void Collect(DependencyObject node, List<UIElement> marked)
    {
        if (node is UIElement element && GetStep(element) > 0)
        {
            marked.Add(element);
        }

        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject next)
            {
                Collect(next, marked);
            }
        }
    }
}
