using System.Windows;
using System.Windows.Media;

namespace CodeSwitchX.UI.Telemetry;

/// <summary>
/// One of the plan's limits as its bar draws it (#384): how much is spent, how much of the window has passed (null when no
/// reset was read) and a short text beside it.
/// </summary>
public sealed record LimitBarState(double Percent, double? Pace, string Text)
{
    /// <summary>
    /// How many points past the pace the spent part may run before it counts as ahead: work comes in bursts, and early in a
    /// window one ordinary turn runs past the mark (4 % spent ten minutes into five hours) without the limit being at risk.
    /// </summary>
    public const double AheadMargin = 10;

    /// <summary>Spent clearly faster than the window passes: at this rate the limit runs out before it resets.</summary>
    public bool IsAhead => Pace is { } pace && Percent > pace + AheadMargin;
}

/// <summary>
/// A thin bar for one of the plan's limits (#384): a track, the part spent, and a mark for how much of the window has passed.
/// The spent part takes <see cref="AheadBrush"/> when it runs past the mark.
/// </summary>
public sealed class LimitBar : FrameworkElement
{
    /// <summary>How tall the track is; the pace mark takes the element's full height.</summary>
    internal const double TrackHeight = 6;

    /// <summary>How wide the pace mark is.</summary>
    internal const double MarkWidth = 2;

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(LimitBarState), typeof(LimitBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = BrushProperty(nameof(TrackBrush), Color.FromRgb(0x37, 0x41, 0x51));

    public static readonly DependencyProperty FillBrushProperty = BrushProperty(nameof(FillBrush), Color.FromRgb(0x3B, 0x82, 0xF6));

    public static readonly DependencyProperty AheadBrushProperty = BrushProperty(nameof(AheadBrush), Color.FromRgb(0xF5, 0x9E, 0x0B));

    public static readonly DependencyProperty MarkBrushProperty = BrushProperty(nameof(MarkBrush), Color.FromRgb(0xE5, 0xE7, 0xEB));

    public LimitBarState? State
    {
        get => (LimitBarState?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush AheadBrush
    {
        get => (Brush)GetValue(AheadBrushProperty);
        set => SetValue(AheadBrushProperty, value);
    }

    public Brush MarkBrush
    {
        get => (Brush)GetValue(MarkBrushProperty);
        set => SetValue(MarkBrushProperty, value);
    }

    private static DependencyProperty BrushProperty(string name, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return DependencyProperty.Register(name, typeof(Brush), typeof(LimitBar), new FrameworkPropertyMetadata(brush, FrameworkPropertyMetadataOptions.AffectsRender));
    }

    /// <summary>Where the spent part ends, from the left, on a bar this wide.</summary>
    internal static double FillWidth(LimitBarState state, double width) => Math.Clamp(state.Percent, 0, 100) / 100 * width;

    /// <summary>
    /// Where the pace mark's left edge sits on a bar this wide, kept inside it and on a whole device pixel at this
    /// <paramref name="scale"/> (a mark between pixels is drawn blurred over three); null without a mark.
    /// </summary>
    internal static double? MarkLeft(LimitBarState state, double width, double scale = 1)
    {
        if (state.Pace is not { } pace)
        {
            return null;
        }

        var left = Math.Clamp((Math.Clamp(pace, 0, 100) / 100 * width) - (MarkWidth / 2), 0, Math.Max(0, width - MarkWidth));
        return Math.Round(left * scale) / scale;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var track = Math.Min(TrackHeight, height);
        var top = (height - track) / 2;
        var radius = track / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, top, width, track), radius, radius);
        if (State is not { } state)
        {
            return;
        }

        var fill = FillWidth(state, width);
        if (fill > 0)
        {
            // Clipped to the track's shape, so a small part keeps the track's round left end instead of a round blob of its own.
            dc.PushClip(new RectangleGeometry(new Rect(0, top, width, track), radius, radius));
            dc.DrawRectangle(state.IsAhead ? AheadBrush : FillBrush, null, new Rect(0, top, fill, track));
            dc.Pop();
        }

        if (MarkLeft(state, width, VisualTreeHelper.GetDpi(this).DpiScaleX) is { } left)
        {
            dc.DrawRectangle(MarkBrush, null, new Rect(left, 0, MarkWidth, height));
        }
    }
}
