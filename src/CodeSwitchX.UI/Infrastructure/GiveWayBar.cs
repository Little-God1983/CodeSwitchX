using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>
/// A row that gives way on a narrow window instead of running its items into each other (#168): the elements inside it
/// marked with a <see cref="StepProperty"/> fold (collapse) in that order, step 1 first, each step only when the row would
/// not fit the width without it. A wide row folds nothing. The row is measured unlimited in width, so what folds comes
/// from the width the bar is given and the items' own widths, never from the row's folded size. Marked elements are found
/// in the bar's logical tree (its content, not inside templates). Folding sets a current value, so an element's own
/// Visibility binding or style is kept and comes back when it unfolds.
/// </summary>
public sealed class GiveWayBar : Decorator
{
    public static readonly DependencyProperty StepProperty = DependencyProperty.RegisterAttached("Step", typeof(int), typeof(GiveWayBar),
        new PropertyMetadata(0, OnStepChanged));

    /// <summary>Within this much a row still fits: layout rounding (125 % scaling) can leave a sum a hair over the width.</summary>
    private const double Slack = 0.01;

    /// <summary>The marked elements, found once; a new child or a step set later finds them again.</summary>
    private List<UIElement>? _marked;

    /// <summary>The elements this bar collapsed, to give back their own Visibility when they unfold.</summary>
    private readonly HashSet<UIElement> _folded = [];

    /// <summary>
    /// How much narrower the row got when each step last folded, by step: a measure judges from it whether the step would
    /// fit again, without unfolding it to see.
    /// </summary>
    private double[] _saved = [];

    /// <summary>When the element folds: 1 first, then 2, and so on; 0 (the default) never.</summary>
    public static int GetStep(DependencyObject element) => (int)element.GetValue(StepProperty);

    public static void SetStep(DependencyObject element, int value) => element.SetValue(StepProperty, value);

    /// <summary>How many steps the last measure folded: 0 none.</summary>
    public int Folded { get; private set; }

    /// <summary>Whether a row this wide fits the width, give or take layout rounding.</summary>
    internal static bool Fits(double row, double width) => row <= width + Slack;

    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        Forget();
    }

    /// <summary>
    /// Starts from the steps the last measure folded: folds the next while the row is too wide, and unfolds the last while
    /// what it saved would fit again. A row that keeps its fold measures once and changes no element.
    /// </summary>
    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is not { } child)
        {
            return default;
        }

        var marked = _marked ??= Marked(child);
        var steps = marked.Count == 0 ? 0 : marked.Max(GetStep);
        if (_saved.Length != steps + 1)
        {
            Array.Resize(ref _saved, steps + 1);
        }

        // Measured unlimited in width, the row's desired width is never cut to the constraint, so an item that grows later
        // still reaches this measure and folds the next step.
        var unlimited = new Size(double.PositiveInfinity, constraint.Height);
        double RowWith(int folded)
        {
            Fold(marked, folded);
            child.Measure(unlimited);
            return child.DesiredSize.Width;
        }

        var width = constraint.Width;
        var folded = Math.Min(Folded, steps);
        var row = RowWith(folded);
        while (folded < steps && !Fits(row, width))
        {
            var narrower = RowWith(folded + 1);
            _saved[folded + 1] = row - narrower;
            row = narrower;
            folded++;
        }

        while (folded > 0 && Fits(row + _saved[folded], width))
        {
            var wider = RowWith(folded - 1);
            if (!Fits(wider, width))
            {
                // What it saved has grown since it folded: it stays folded, and the next measure knows.
                _saved[folded] = wider - row;
                row = RowWith(folded);
                break;
            }

            row = wider;
            folded--;
        }

        Folded = folded;
        return new Size(Math.Min(row, width), child.DesiredSize.Height);
    }

    private static void OnStepChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        for (var up = ParentOf(element); up is not null; up = ParentOf(up))
        {
            if (up is GiveWayBar bar)
            {
                bar.Forget();
                return;
            }
        }
    }

    /// <summary>Gives every folded element back its own Visibility and finds the marked elements again on the next measure.</summary>
    private void Forget()
    {
        foreach (var element in _folded)
        {
            element.InvalidateProperty(VisibilityProperty);
        }

        _folded.Clear();
        _marked = null;
        _saved = [];
        Folded = 0;
        InvalidateMeasure();
    }

    /// <summary>Folds the marked elements of the first <paramref name="folded"/> steps and gives the rest their own Visibility.</summary>
    private void Fold(List<UIElement> marked, int folded)
    {
        foreach (var element in marked)
        {
            if (GetStep(element) <= folded)
            {
                // Already collapsed by its own binding there is nothing to fold; shown again by it, it folds once more.
                if (element.Visibility == Visibility.Collapsed)
                {
                    continue;
                }

                element.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
                _folded.Add(element);
            }
            else if (_folded.Remove(element))
            {
                element.InvalidateProperty(VisibilityProperty);
            }
            else
            {
                continue;
            }

            // A changed element marks only its parent for a new measure: everything up to the bar must measure again, or a
            // parent that was not marked returns its old size.
            for (var up = ParentOf(element); up is not null && up != this; up = ParentOf(up))
            {
                (up as UIElement)?.InvalidateMeasure();
            }
        }
    }

    /// <summary>The visual parent, or the logical one where the visual tree is not made yet (a button's content before its template).</summary>
    private static DependencyObject? ParentOf(DependencyObject node) =>
        (node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : null) ?? LogicalTreeHelper.GetParent(node);

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
