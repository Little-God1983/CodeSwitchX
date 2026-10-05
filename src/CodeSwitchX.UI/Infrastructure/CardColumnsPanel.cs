using System.Windows;
using System.Windows.Controls;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>
/// Cards side by side in as many equal columns as fit the width it is given, at least <see cref="MinColumnWidth"/> wide
/// each, one to <see cref="MaxColumns"/>; each row as tall as its tallest card. On a narrow page the cards go under each
/// other instead of being cut (#155). The count comes from the width the panel is measured with, so it never feeds on its
/// own size.
/// </summary>
public sealed class CardColumnsPanel : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(nameof(MinColumnWidth), typeof(double),
        typeof(CardColumnsPanel), new FrameworkPropertyMetadata(200.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(nameof(MaxColumns), typeof(int),
        typeof(CardColumnsPanel), new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double),
        typeof(CardColumnsPanel), new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The narrowest a column may be; fewer columns are used before one gets narrower.</summary>
    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>The space between columns and between rows; none at the edges.</summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>How many columns fit the width: one to <see cref="MaxColumns"/>, all of them when the width is not limited.</summary>
    internal static int ColumnsFor(double width, double minColumnWidth, int maxColumns, double gap)
    {
        var most = Math.Max(1, maxColumns);
        return double.IsInfinity(width) || double.IsNaN(width) ? most
            : Math.Clamp((int)Math.Floor((width + gap) / (Math.Max(1, minColumnWidth) + gap)), 1, most);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;
        var columns = ColumnsFor(availableSize.Width, MinColumnWidth, MaxColumns, Gap);
        var width = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : ColumnWidth(availableSize.Width, columns);
        var widest = 0.0;
        foreach (UIElement child in children)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            widest = Math.Max(widest, child.DesiredSize.Width);
        }

        var column = double.IsInfinity(width) ? widest : width;
        return new Size(column * columns + Gap * (columns - 1), RowsHeight(columns));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        var columns = ColumnsFor(finalSize.Width, MinColumnWidth, MaxColumns, Gap);
        var width = ColumnWidth(finalSize.Width, columns);
        var top = 0.0;
        for (var start = 0; start < children.Count; start += columns)
        {
            var height = RowHeight(start, columns);
            for (var i = start; i < Math.Min(start + columns, children.Count); i++)
            {
                children[i].Arrange(new Rect((i - start) * (width + Gap), top, width, height));
            }

            top += height + Gap;
        }

        return finalSize;
    }

    private double ColumnWidth(double width, int columns) => Math.Max(0, (width - Gap * (columns - 1)) / columns);

    private double RowHeight(int start, int columns)
    {
        var height = 0.0;
        for (var i = start; i < Math.Min(start + columns, InternalChildren.Count); i++)
        {
            height = Math.Max(height, InternalChildren[i].DesiredSize.Height);
        }

        return height;
    }

    private double RowsHeight(int columns)
    {
        var rows = (InternalChildren.Count + columns - 1) / columns;
        var height = 0.0;
        for (var row = 0; row < rows; row++)
        {
            height += RowHeight(row * columns, columns);
        }

        return height + Gap * Math.Max(0, rows - 1);
    }
}
