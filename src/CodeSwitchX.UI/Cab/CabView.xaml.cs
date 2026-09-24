using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.UI.Cab;

public partial class CabView : UserControl
{
    private ScreenRect? _lastRect;
    private Window? _window;

    public CabView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) => Publish();
        HostArea.SizeChanged += (_, _) => Publish();
    }

    /// <summary>Screen rectangle (physical pixels) of the host area, raised whenever it changes.</summary>
    public event Action<ScreenRect>? HostRectChanged;

    private void Attach()
    {
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.LocationChanged += OnWindowMoved;
            _window.StateChanged += OnWindowMoved;
        }

        Publish();
    }

    private void Detach()
    {
        if (_window is not null)
        {
            _window.LocationChanged -= OnWindowMoved;
            _window.StateChanged -= OnWindowMoved;
            _window = null;
        }
    }

    private void OnWindowMoved(object? sender, EventArgs e) => Publish();

    private void Publish()
    {
        if (!IsVisible || _window?.WindowState == WindowState.Minimized || HostArea.ActualWidth <= 0 || HostArea.ActualHeight <= 0
            || PresentationSource.FromVisual(HostArea) is not { } source)
        {
            return; // a minimised window reports an off-screen rectangle; the shell hides VS Code instead
        }

        var scale = source.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var topLeft = HostArea.PointToScreen(new Point(0, 0));
        var rect = ScreenRect.FromSize(
            (int)Math.Round(topLeft.X),
            (int)Math.Round(topLeft.Y),
            (int)Math.Round(HostArea.ActualWidth * scale.M11),
            (int)Math.Round(HostArea.ActualHeight * scale.M22));

        if (rect != _lastRect)
        {
            _lastRect = rect;
            HostRectChanged?.Invoke(rect);
        }
    }
}
