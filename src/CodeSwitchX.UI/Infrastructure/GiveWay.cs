using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>
/// An item of a <see cref="GiveWayBar"/> that folds at its <see cref="Step"/>. Folded, it takes no room and shows nothing,
/// but it still measures its content, so the bar always knows how wide the item would be shown: an item that shrank while
/// folded comes back as soon as it fits again. Its content's own Visibility is left alone.
/// </summary>
public sealed class GiveWay : Decorator
{
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(int), typeof(GiveWay),
        new PropertyMetadata(0, (item, _) => ((GiveWay)item)._bar?.InvalidateMeasure()));

    private GiveWayBar? _bar;

    private bool _folded;

    /// <summary>When the item folds: 1 first, then 2, and so on; 0 (the default) never.</summary>
    public int Step
    {
        get => (int)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    /// <summary>How wide the item is shown, folded or not.</summary>
    internal double Natural { get; private set; }

    internal bool IsFolded
    {
        get => _folded;
        set
        {
            if (_folded == value)
            {
                return;
            }

            _folded = value;
            // A changed item marks only its parent for a new measure: everything up to the bar must measure again, or a
            // parent that was not marked returns its old size.
            for (DependencyObject? up = this; up is not null && up != _bar; up = GiveWayBar.ParentOf(up))
            {
                (up as UIElement)?.InvalidateMeasure();
            }

            InvalidateArrange();
        }
    }

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        // Taken out of its row it leaves at once: folded it took no room, so the row's size would not tell the bar.
        Join(VisualTreeHelper.GetParent(this) is null ? null : GiveWayBar.Above(this));
    }

    protected override Size MeasureOverride(Size constraint)
    {
        Join(GiveWayBar.Above(this));
        if (Child is not { } child)
        {
            Natural = 0;
            return default;
        }

        child.Measure(constraint);
        var natural = child.DesiredSize.Width;
        if (_folded && natural != Natural)
        {
            // Folded, its new width reaches no one through the row: the bar must judge again.
            _bar?.InvalidateMeasure();
        }

        Natural = natural;
        return _folded ? default : child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(_folded ? default : new Rect(arrangeSize));
        return _folded ? default : arrangeSize;
    }

    /// <summary>Folded, nothing of it shows, nor takes a click.</summary>
    protected override Geometry? GetLayoutClip(Size layoutSlotSize) => _folded ? Geometry.Empty : base.GetLayoutClip(layoutSlotSize);

    /// <summary>Its bar let it go: shown again, and it joins whichever bar it measures under next.</summary>
    internal void LetGo()
    {
        _bar = null;
        _folded = false;
        InvalidateMeasure();
    }

    private void Join(GiveWayBar? bar)
    {
        if (bar == _bar)
        {
            return;
        }

        // A new bar (or none) starts it shown; the bar folds it again if its row needs that.
        _bar?.Leave(this);
        _bar = bar;
        _folded = false;
        bar?.Join(this);
    }
}
