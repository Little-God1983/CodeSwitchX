using System.Windows;
using System.Windows.Media;

namespace CodeSwitchX.UI.Raven;

/// <summary>
/// Raven's orb: a glow, a wave ring that follows the mic level while listening, arcs that turn while transcribing and a
/// gradient core. It draws itself and animates on <see cref="CompositionTarget.Rendering"/> only while it can be seen
/// and is asked to animate; with animations turned off in Windows it draws one still frame per change. The geometry
/// follows the concept page's canvas, whose 336 px square maps onto the element's size.
/// </summary>
public sealed class RavenOrb : FrameworkElement
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(RavenState), typeof(RavenOrb),
        new FrameworkPropertyMetadata(RavenState.Idle, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(RavenOrb),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsAnimatingProperty = DependencyProperty.Register(nameof(IsAnimating), typeof(bool), typeof(RavenOrb),
        new FrameworkPropertyMetadata(false, (d, _) => ((RavenOrb)d).UpdateHook()));

    private const double CanvasSize = 336;
    private const double Base = 62;

    /// <summary>Idle only breathes a few pixels; half the display's frame rate draws that just as smoothly.</summary>
    private static readonly TimeSpan IdleFrame = TimeSpan.FromMilliseconds(33);

    private static readonly Color Voice = Color.FromRgb(0x62, 0xD0, 0xE8);

    private Window? _window;
    private bool _hooked;
    private TimeSpan _lastTick = TimeSpan.MinValue;
    private TimeSpan _lastDraw;
    private double _seconds;
    private double _shownLevel;
    private Size _coreBrushSize;
    private RadialGradientBrush? _coreBrush;

    public RavenOrb()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => OnLoaded();
        Unloaded += (_, _) => OnUnloaded();
        IsVisibleChanged += (_, _) => UpdateHook();
    }

    public RavenState State
    {
        get => (RavenState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>0..1: the mic level while listening.</summary>
    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public bool IsAnimating
    {
        get => (bool)GetValue(IsAnimatingProperty);
        set => SetValue(IsAnimatingProperty, value);
    }

    private static bool MotionAllowed => SystemParameters.ClientAreaAnimation;

    private bool ShouldAnimate => IsAnimating && IsVisible && MotionAllowed && _window is not null && _window.WindowState != WindowState.Minimized;

    private void OnLoaded()
    {
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.StateChanged += OnWindowStateChanged;
        }

        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        UpdateHook();
    }

    private void OnUnloaded()
    {
        if (_window is not null)
        {
            _window.StateChanged -= OnWindowStateChanged;
            _window = null;
        }

        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        UpdateHook();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateHook();

    private void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
        {
            UpdateHook();
            InvalidateVisual();
        }
    }

    private void UpdateHook()
    {
        var animate = ShouldAnimate;
        if (animate == _hooked)
        {
            return;
        }

        _hooked = animate;
        if (animate)
        {
            _lastTick = TimeSpan.MinValue;
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
            InvalidateVisual(); // the still frame
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs rendering ? rendering.RenderingTime : TimeSpan.Zero;
        if (now == _lastTick)
        {
            return; // Rendering can be raised more than once for one frame
        }

        var dt = _lastTick == TimeSpan.MinValue ? 0 : Math.Clamp((now - _lastTick).TotalSeconds, 0, 0.1);
        _lastTick = now;
        _seconds += dt;

        // The level arrives in 50 ms steps; easing towards it keeps the ring from jumping (0.18 per 60 Hz frame).
        var target = State == RavenState.Listening ? Math.Clamp(Level, 0, 1) : 0;
        _shownLevel += (target - _shownLevel) * (1 - Math.Pow(1 - 0.18, dt * 60));

        if (State == RavenState.Idle && now - _lastDraw < IdleFrame)
        {
            return;
        }

        _lastDraw = now;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var scale = size / CanvasSize;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var t = _hooked ? _seconds : 0;
        var amp = State == RavenState.Listening ? (_hooked ? _shownLevel : Math.Clamp(Level, 0, 1)) : 0;
        var breathe = State == RavenState.Idle ? Math.Sin(t * 1.4) * 3 : 0;

        DrawGlow(dc, center, scale, amp);
        DrawWaveRing(dc, center, scale, t, amp, breathe);
        if (State == RavenState.Transcribing)
        {
            DrawArcs(dc, center, scale, t);
        }
        else
        {
            dc.DrawEllipse(null, VoicePen(0.25, Math.Max(0.75, 1.4 * scale)), center, (Base + 44) * scale, (Base + 44) * scale);
        }

        var core = (Base - 4 + breathe + amp * 6) * scale;
        dc.DrawEllipse(CoreBrush(center, scale), null, center, core, core);
    }

    private static void DrawGlow(DrawingContext dc, Point center, double scale, double amp)
    {
        var radius = 160 * scale;
        var glow = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = center,
            GradientOrigin = center,
            RadiusX = radius,
            RadiusY = radius,
            GradientStops =
            {
                new GradientStop(WithAlpha(0.28 + amp * 0.3), 10.0 / 160),
                new GradientStop(WithAlpha(0), 1),
            },
        };
        glow.Freeze();
        dc.DrawEllipse(glow, null, center, radius, radius);
    }

    /// <summary>A closed ring whose radius is the sum of three sines, pushed out by the level.</summary>
    private static void DrawWaveRing(DrawingContext dc, Point center, double scale, double t, double amp, double breathe)
    {
        const int Steps = 120;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var i = 0; i < Steps; i++)
            {
                var a = i / (double)Steps * Math.PI * 2;
                var n = Math.Sin(a * 6 + t * 3) * 0.5 + Math.Sin(a * 11 - t * 4.3) * 0.35 + Math.Sin(a * 3 + t * 1.7) * 0.4;
                var r = (Base + 14 + breathe + n * amp * 26) * scale;
                var point = new Point(center.X + Math.Cos(a) * r, center.Y + Math.Sin(a) * r);
                if (i == 0)
                {
                    ctx.BeginFigure(point, isFilled: false, isClosed: true);
                }
                else
                {
                    ctx.LineTo(point, isStroked: true, isSmoothJoin: true);
                }
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, VoicePen(0.9, Math.Max(1, 2.5 * scale)), geometry);
    }

    private static void DrawArcs(DrawingContext dc, Point center, double scale, double t)
    {
        for (var k = 0; k < 3; k++)
        {
            var radius = (Base + 34 + k * 10) * scale;
            var start = t * (2.2 + k * 0.9) + k * 2;
            var end = start + 1.1 + k * 0.4;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(center.X + Math.Cos(start) * radius, center.Y + Math.Sin(start) * radius), isFilled: false, isClosed: false);
                ctx.ArcTo(new Point(center.X + Math.Cos(end) * radius, center.Y + Math.Sin(end) * radius), new Size(radius, radius), 0,
                    isLargeArc: false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
            }

            geometry.Freeze();
            dc.DrawGeometry(null, VoicePen(0.8 - k * 0.22, Math.Max(1, 3 * scale)), geometry);
        }
    }

    /// <summary>The core's gradient only depends on the size; it is built again when that changes.</summary>
    private RadialGradientBrush CoreBrush(Point center, double scale)
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (_coreBrush is null || _coreBrushSize != size)
        {
            _coreBrushSize = size;
            _coreBrush = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = center,
                GradientOrigin = new Point(center.X - 14 * scale, center.Y - 14 * scale),
                RadiusX = Base * scale,
                RadiusY = Base * scale,
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0xBF, 0xF0, 0xFA), 0),
                    new GradientStop(Voice, 0.45),
                    new GradientStop(Color.FromRgb(0x0B, 0x34, 0x40), 1),
                },
            };
            _coreBrush.Freeze();
        }

        return _coreBrush;
    }

    private static Color WithAlpha(double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), Voice.R, Voice.G, Voice.B);

    private static Pen VoicePen(double alpha, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(WithAlpha(alpha)), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }
}
