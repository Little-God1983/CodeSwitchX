using System.Windows;
using System.Windows.Media;

namespace CodeSwitchX.UI.Raven;

/// <summary>
/// Raven's orb: a glow, a wave ring that follows the mic level while listening, arcs that turn while transcribing, dots
/// that go round while Raven's brain thinks, rings that go out from a wave ring following the voice while Raven speaks,
/// a ring of dots with a glint going round while Open mic waits, and a gradient core. It draws itself and animates on <see cref="CompositionTarget.Rendering"/> only while it can be seen
/// and is asked to animate; with animations turned off in Windows it draws one still frame per change. Idle breathing
/// also stops while its window is not the active one (VS Code docked in front, say): listening, transcribing, thinking
/// and speaking keep animating, because they show what the microphone and the models are doing. The geometry follows the concept page's
/// canvas, whose 336 px square maps onto the element's size.
/// <para>
/// Each frame is drawn into a <see cref="DrawingGroup"/> that <see cref="OnRender"/> hands to WPF once. Redrawing that
/// group updates the screen without InvalidateVisual, which would arrange the element again on every frame and so run
/// a layout pass, and every LayoutUpdated handler in the window, some thirty times a second.
/// </para>
/// <para>
/// Only the wave ring's geometry is built per frame. The pens, the glow and the arcs are frozen once and reused: the pens
/// per alpha byte (a handful of fixed alphas), the glow per alpha byte of its centre (the level moves it through some
/// eighty values) and the arcs per scale, turned into place by a transform. Pens and arcs are dropped when the scale
/// changes; the glow is mapped to the ellipse it fills and does not depend on the size.
/// </para>
/// </summary>
public sealed class RavenOrb : FrameworkElement
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(RavenState), typeof(RavenOrb),
        new FrameworkPropertyMetadata(RavenState.Idle, (d, _) => ((RavenOrb)d).OnStateChanged()));

    /// <summary>Changes every captured block (10 ms) or played buffer (60 ms): redrawn by the next frame while animating, straight away otherwise.</summary>
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(RavenOrb),
        new FrameworkPropertyMetadata(0.0, (d, _) => ((RavenOrb)d).OnInputChanged()));

    public static readonly DependencyProperty IsAnimatingProperty = DependencyProperty.Register(nameof(IsAnimating), typeof(bool), typeof(RavenOrb),
        new FrameworkPropertyMetadata(false, (d, _) => ((RavenOrb)d).UpdateHook()));

    private const double CanvasSize = 336;
    private const double Base = 62;

    /// <summary>Idle only breathes a few pixels; half the display's frame rate draws that just as smoothly.</summary>
    private static readonly TimeSpan IdleFrame = TimeSpan.FromMilliseconds(33);

    private static readonly Color Voice = Color.FromRgb(0x62, 0xD0, 0xE8);

    private readonly DrawingGroup _frame = new();
    private Window? _window;
    private bool _hooked;
    private TimeSpan _lastTick = TimeSpan.MinValue;
    private TimeSpan _lastDraw;
    private double _seconds;
    private double _shownLevel;

    /// <summary>1 while Open mic waits, easing to 0 as speech takes over: the light ring fades out, the wave ring comes in.</summary>
    private double _attend;
    private readonly Dictionary<byte, Brush> _dots = [];
    private Size _coreBrushSize;
    private RadialGradientBrush? _coreBrush;
    private SolidColorBrush? _dot;
    private readonly Dictionary<(byte Alpha, double Thickness), Pen> _pens = [];
    private readonly Dictionary<byte, Brush> _glows = [];
    private double _cachedScale = double.NaN;
    private StreamGeometry[]? _arcs;

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

    private bool ShouldAnimate => IsAnimating && IsVisible && MotionAllowed && _window is not null && _window.WindowState != WindowState.Minimized
        && State != RavenState.AttendingPaused && (_window.IsActive || State != RavenState.Idle);

    private void OnLoaded()
    {
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.StateChanged += OnWindowStateChanged;
            _window.Activated += OnWindowStateChanged;
            _window.Deactivated += OnWindowStateChanged;
        }

        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        UpdateHook();
    }

    private void OnUnloaded()
    {
        if (_window is not null)
        {
            _window.StateChanged -= OnWindowStateChanged;
            _window.Activated -= OnWindowStateChanged;
            _window.Deactivated -= OnWindowStateChanged;
            _window = null;
        }

        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        UpdateHook();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateHook();

    private void OnStateChanged()
    {
        UpdateHook();
        OnInputChanged();
    }

    /// <summary>While animating the next frame shows the change; still, the one frame is drawn again now.</summary>
    private void OnInputChanged()
    {
        if (!_hooked)
        {
            DrawFrame();
        }
    }

    private void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
        {
            UpdateHook();
            DrawFrame();
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
            DrawFrame(); // the still frame
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
        var target = State is RavenState.Listening or RavenState.Speaking or RavenState.Attending ? Math.Clamp(Level, 0, 1) : 0;
        _shownLevel += (target - _shownLevel) * (1 - Math.Pow(1 - 0.18, dt * 60));
        var attending = State is RavenState.Attending or RavenState.AttendingPaused ? 1 : 0;
        _attend += (attending - _attend) * (1 - Math.Pow(1 - 0.12, dt * 60));

        // Thinking and the light ring move as calmly as the idle breath, and may last for hours in a background window.
        if (State is RavenState.Idle or RavenState.Thinking or RavenState.Attending && now - _lastDraw < IdleFrame)
        {
            return;
        }

        _lastDraw = now;
        DrawFrame();
    }

    /// <summary>Called again by WPF only when the element is arranged at a new size; the frames go through the group.</summary>
    protected override void OnRender(DrawingContext drawingContext)
    {
        DrawFrame();
        drawingContext.DrawDrawing(_frame);
    }

    private void DrawFrame()
    {
        using var dc = _frame.Open();
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var scale = size / CanvasSize;
        if (scale != _cachedScale)
        {
            _cachedScale = scale;
            _pens.Clear();
            _arcs = null;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var t = _hooked ? _seconds : 0;
        var attend = _hooked ? _attend : State is RavenState.Attending or RavenState.AttendingPaused ? 1 : 0;
        var room = State == RavenState.Attending ? Math.Min(_hooked ? _shownLevel : Math.Clamp(Level, 0, 1), 0.3) : 0;
        var amp = State switch
        {
            RavenState.Listening or RavenState.Speaking => _hooked ? _shownLevel : Math.Clamp(Level, 0, 1),
            RavenState.Thinking => 0.18 + (Math.Sin(t * 2.4) * 0.12), // a slow ripple: no voice moves it
            _ => 0,
        };
        var breathe = State == RavenState.Idle ? Math.Sin(t * 1.4) * 3 : 0;

        DrawGlow(dc, center, scale, amp + (room * 0.3));
        DrawWaveRing(dc, center, scale, t, amp, breathe, 0.9 - (0.35 * attend));
        if (State == RavenState.Transcribing)
        {
            DrawArcs(dc, center, scale, t);
        }
        else if (State == RavenState.Thinking)
        {
            DrawOrbit(dc, center, scale, t);
        }
        else if (State == RavenState.Speaking)
        {
            DrawRipples(dc, center, scale, t, amp);
        }
        else if (attend < 0.99)
        {
            dc.DrawEllipse(null, VoicePen(0.25 * (1 - attend), Math.Max(0.75, 1.4 * scale)), center, (Base + 44) * scale, (Base + 44) * scale);
        }

        if (attend > 0.01)
        {
            DrawLightRing(dc, center, scale, t, room, attend, State == RavenState.AttendingPaused);
        }

        var core = (Base - 4 + breathe + amp * 6) * scale;
        dc.DrawEllipse(CoreBrush(center, scale), null, center, core, core);
    }

    /// <summary>
    /// The glow brush keeps its default mapping (relative to the ellipse it fills: centred, radius a half), which draws
    /// the same as one mapped to the absolute centre and radius, so it only depends on the alpha of its centre.
    /// </summary>
    private void DrawGlow(DrawingContext dc, Point center, double scale, double amp)
    {
        var radius = 160 * scale;
        var alpha = AlphaByte(0.28 + amp * 0.3);
        if (!_glows.TryGetValue(alpha, out var glow))
        {
            glow = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(alpha, Voice.R, Voice.G, Voice.B), 10.0 / 160),
                    new GradientStop(WithAlpha(0), 1),
                },
            };
            glow.Freeze();
            _glows[alpha] = glow;
        }

        dc.DrawEllipse(glow, null, center, radius, radius);
    }

    /// <summary>A closed ring whose radius is the sum of three sines, pushed out by the level.</summary>
    private void DrawWaveRing(DrawingContext dc, Point center, double scale, double t, double amp, double breathe, double alpha)
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
        dc.DrawGeometry(null, VoicePen(alpha, Math.Max(1, 2.5 * scale)), geometry);
    }

    /// <summary>Three arcs of fixed length that turn: each is built once per scale, starting at angle 0 around the origin,
    /// and turned to its start angle and moved to the centre as it is drawn.</summary>
    private void DrawArcs(DrawingContext dc, Point center, double scale, double t)
    {
        _arcs ??= [ArcGeometry(0, scale), ArcGeometry(1, scale), ArcGeometry(2, scale)];
        for (var k = 0; k < 3; k++)
        {
            var start = t * (2.2 + k * 0.9) + k * 2;
            var placement = Matrix.Identity;
            placement.Rotate(start * 180 / Math.PI);
            placement.Translate(center.X, center.Y);
            var transform = new MatrixTransform(placement);
            transform.Freeze();
            dc.PushTransform(transform);
            dc.DrawGeometry(null, VoicePen(0.8 - k * 0.22, Math.Max(1, 3 * scale)), _arcs[k]);
            dc.Pop();
        }
    }

    /// <summary>Two rings that go out from the orb and fade, brighter the louder the voice: Raven is talking.</summary>
    private void DrawRipples(DrawingContext dc, Point center, double scale, double t, double amp)
    {
        for (var k = 0; k < 2; k++)
        {
            var phase = ((t * 0.7) + (k * 0.5)) % 1;
            var radius = (Base + 30 + (phase * 44)) * scale;
            dc.DrawEllipse(null, VoicePen((1 - phase) * (0.25 + (amp * 0.5)), Math.Max(0.75, 1.6 * scale)), center, radius, radius);
        }
    }

    /// <summary>The outer ring, with three dots going round it: Raven's brain is at work.</summary>
    private void DrawOrbit(DrawingContext dc, Point center, double scale, double t)
    {
        var radius = (Base + 44) * scale;
        dc.DrawEllipse(null, VoicePen(0.25, Math.Max(0.75, 1.4 * scale)), center, radius, radius);
        _dot ??= Frozen(new SolidColorBrush(Voice));
        for (var k = 0; k < 3; k++)
        {
            var a = (t * 1.6) + (k * Math.PI * 2 / 3);
            var dot = Math.Max(1.5, (3.5 - k * 0.6) * scale);
            dc.DrawEllipse(_dot, null, new Point(center.X + (Math.Cos(a) * radius), center.Y + (Math.Sin(a) * radius)), dot, dot);
        }
    }

    /// <summary>
    /// Open mic's light ring: 36 dim dots on the outer ring with a soft glint drifting round them, and the room's sound
    /// lighting them unevenly, each by its own flicker, so a sound shows as a sparkle round the ring. Paused: the dots
    /// still, at half their light, with no glint. <paramref name="fade"/> takes it out as speech starts.
    /// </summary>
    private void DrawLightRing(DrawingContext dc, Point center, double scale, double t, double room, double fade, bool paused)
    {
        const int Dots = 36;
        var radius = (Base + 44) * scale;
        var glint = t * 0.9;
        for (var i = 0; i < Dots; i++)
        {
            var a = (i / (double)Dots * Math.PI * 2) - (Math.PI / 2);
            var d = Math.Abs(((((a - glint) % (Math.PI * 2)) + (Math.PI * 3)) % (Math.PI * 2)) - Math.PI);
            var g = paused ? 0 : Math.Exp(-d * d * 2.2);
            var flicker = 0.5 + (0.5 * Math.Sin((i * 2.7) + (t * 9)));
            var lit = paused ? 0.08 : 0.16 + (g * 0.55) + (room * 2.2 * flicker);
            var size = Math.Max(0.6, (1.6 + (g * 1.4) + (room * 3 * flicker)) * scale);
            dc.DrawEllipse(DotBrush(lit * fade), null, new Point(center.X + (Math.Cos(a) * radius), center.Y + (Math.Sin(a) * radius)), size, size);
        }
    }

    private Brush DotBrush(double alpha)
    {
        var key = AlphaByte(alpha);
        if (!_dots.TryGetValue(key, out var brush))
        {
            brush = Frozen(new SolidColorBrush(Color.FromArgb(key, Voice.R, Voice.G, Voice.B)));
            _dots[key] = brush;
        }

        return brush;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static StreamGeometry ArcGeometry(int k, double scale)
    {
        var radius = (Base + 34 + k * 10) * scale;
        var sweep = 1.1 + k * 0.4;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(radius, 0), isFilled: false, isClosed: false);
            ctx.ArcTo(new Point(Math.Cos(sweep) * radius, Math.Sin(sweep) * radius), new Size(radius, radius), 0,
                isLargeArc: false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
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

    private static byte AlphaByte(double alpha) => (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255);

    private static Color WithAlpha(double alpha) => Color.FromArgb(AlphaByte(alpha), Voice.R, Voice.G, Voice.B);

    /// <summary>One frozen pen per alpha byte and thickness; the thicknesses follow the scale, whose change clears them.</summary>
    private Pen VoicePen(double alpha, double thickness)
    {
        var key = (AlphaByte(alpha), thickness);
        if (!_pens.TryGetValue(key, out var pen))
        {
            var brush = new SolidColorBrush(Color.FromArgb(key.Item1, Voice.R, Voice.G, Voice.B));
            brush.Freeze();
            pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            _pens[key] = pen;
        }

        return pen;
    }
}
