using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>
/// A streak of light that runs round a rounded border while <see cref="IsRunning"/> (#276: a working tile on the Yard).
/// Laid over the border, the same size, it draws the streak along the border's centre line: one dash of a pen whose
/// pattern is exactly as long as the border, so one streak shows, its tail fainter than its head. The pens' dash offset is
/// animated, not the drawing: WPF moves the streak without a redraw or a layout pass each frame. With animations turned
/// off in Windows the border is lit still instead. Collapsed or unloaded, it draws nothing and stops its clocks; a tile
/// merely scrolled out of view keeps them, as WPF still counts it visible.
/// </summary>
public sealed class RunningLight : FrameworkElement
{
    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(nameof(IsRunning), typeof(bool), typeof(RunningLight),
        new FrameworkPropertyMetadata(false, (d, _) => ((RunningLight)d).Rebuild()));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(RunningLight),
        new FrameworkPropertyMetadata(Brushes.LimeGreen, (d, _) => ((RunningLight)d).Rebuild()));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(RunningLight),
        new FrameworkPropertyMetadata(2.0, (d, _) => ((RunningLight)d).Rebuild()));

    /// <summary>The corner radius of the border it runs round, as a <see cref="System.Windows.Controls.Border"/> gives it: to the outer edge.</summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius), typeof(double), typeof(RunningLight),
        new FrameworkPropertyMetadata(0.0, (d, _) => ((RunningLight)d).Rebuild()));

    /// <summary>How long the streak takes to go round once.</summary>
    public static readonly TimeSpan LapTime = TimeSpan.FromSeconds(2);

    /// <summary>How much of the border the streak covers, and at most how long it is.</summary>
    internal const double StreakShare = 0.18;
    internal const double MaxStreak = 140;

    /// <summary>The streak's layers, all ending at its head: a wide faint glow, then ever shorter, brighter cores, so the tail fades.</summary>
    private static readonly (double Width, double Length, double Opacity)[] Layers =
        [(3, 0.6, 0.25), (1, 1, 0.35), (1, 0.6, 0.6), (1, 0.3, 1)];

    private readonly DrawingGroup _drawing = new();
    private readonly List<DashStyle> _running = [];

    public RunningLight()
    {
        IsHitTestVisible = false;
        SizeChanged += (_, _) => Rebuild();
        IsVisibleChanged += (_, _) => Rebuild();
        Loaded += (_, _) =>
        {
            // Loaded can come twice without an Unloaded between: one handler, or the static event keeps the tile alive.
            SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
            SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
            Rebuild(); // an Unloaded that came after the last rebuild stopped the clocks: a still streak otherwise
        };
        Unloaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
            Stop();
            _drawing.Open().Close(); // no still streak left behind
        };
    }

    public bool IsRunning
    {
        get => (bool)GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>The length of a rounded rectangle's outline.</summary>
    internal static double Perimeter(double width, double height, double radius) =>
        2 * (width - 2 * radius) + 2 * (height - 2 * radius) + 2 * Math.PI * radius;

    /// <summary>
    /// The dash pattern of a streak <paramref name="length"/> long on an outline <paramref name="perimeter"/> long, and the
    /// dash offsets that take its head once round from the outline's start; all in units of the pen's thickness, as WPF
    /// counts them. A pattern exactly as long as the outline shows one streak. A growing offset moves it against the
    /// outline's own direction: clockwise round the border, as seen on screen.
    /// </summary>
    internal static (double[] Dashes, double From, double To) Streak(double length, double perimeter, double thickness) =>
        ([length / thickness, (perimeter - length) / thickness], length / thickness, (length + perimeter) / thickness);

    protected override void OnRender(DrawingContext drawingContext) => drawingContext.DrawDrawing(_drawing);

    private void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
        {
            Dispatcher.BeginInvoke(Rebuild);
        }
    }

    private void Stop()
    {
        foreach (var dashes in _running)
        {
            dashes.BeginAnimation(DashStyle.OffsetProperty, null);
        }

        _running.Clear();
    }

    private void Rebuild()
    {
        Stop();
        using var context = _drawing.Open();
        var thickness = StrokeThickness;
        var width = ActualWidth - thickness;
        var height = ActualHeight - thickness;
        if (!IsRunning || !IsVisible || width <= 0 || height <= 0 || thickness <= 0)
        {
            return; // an empty drawing
        }

        var radius = Math.Clamp(CornerRadius - thickness / 2, 0, Math.Min(width, height) / 2);
        var outline = new RectangleGeometry(new Rect(thickness / 2, thickness / 2, width, height), radius, radius);
        outline.Freeze();
        if (!SystemParameters.ClientAreaAnimation)
        {
            context.DrawGeometry(null, new Pen(Stroke, thickness), outline);
            return;
        }

        var perimeter = Perimeter(width, height, radius);
        var length = Math.Min(perimeter * StreakShare, MaxStreak);
        foreach (var layer in Layers)
        {
            var penThickness = thickness * layer.Width;
            var (dashes, from, to) = Streak(length * layer.Length, perimeter, penThickness);
            var style = new DashStyle(dashes, from);
            style.BeginAnimation(DashStyle.OffsetProperty, new DoubleAnimation(from, to, LapTime) { RepeatBehavior = RepeatBehavior.Forever });
            _running.Add(style);
            context.PushOpacity(layer.Opacity);
            context.DrawGeometry(null, new Pen(Stroke, penThickness) { DashStyle = style, DashCap = PenLineCap.Flat }, outline);
            context.Pop();
        }
    }
}
