using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodeSwitchX.UI.Yard;

/// <summary>
/// The preview a GitHub pill shows on hover (#335): its heading and the newest rows, in one popup that moves from pill to
/// pill. It opens at once and closes <see cref="CloseDelay"/> after the mouse left the pill and the popup, so the mouse can
/// cross from one to the other.
/// </summary>
public static class GitHubPillPreview
{
    private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(160);

    private static readonly DispatcherTimer CloseTimer = NewTimer();

    private static Popup? _popup;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(GitHubPillPreview), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static DispatcherTimer NewTimer()
    {
        var timer = new DispatcherTimer { Interval = CloseDelay };
        timer.Tick += (_, _) => Close();
        return timer;
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement pill)
        {
            return;
        }

        pill.MouseEnter -= OnPillEnter;
        pill.MouseLeave -= OnLeave;
        pill.Unloaded -= OnPillUnloaded;
        if (e.NewValue is true)
        {
            pill.MouseEnter += OnPillEnter;
            pill.MouseLeave += OnLeave;
            pill.Unloaded += OnPillUnloaded;
        }
    }

    private static void OnPillEnter(object sender, RoutedEventArgs e)
    {
        CloseTimer.Stop();
        if (sender is not FrameworkElement { DataContext: GitHubPill pill } target || Application.Current is not { } app)
        {
            return;
        }

        var popup = _popup ??= NewPopup();
        popup.IsOpen = false;
        popup.PlacementTarget = target;
        popup.Child = Content(pill, (DataTemplate)app.FindResource("GitHubRowTemplate"));
        popup.IsOpen = true;
    }

    private static void OnLeave(object sender, RoutedEventArgs e) => CloseTimer.Start();

    /// <summary>A pill that leaves the tree (the tile is rebuilt, the view closes) takes the preview with it, unless the popup is on another pill by now.</summary>
    private static void OnPillUnloaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(_popup?.PlacementTarget, sender))
        {
            Close();
        }
    }

    private static void Close()
    {
        CloseTimer.Stop();
        if (_popup is { } popup)
        {
            popup.IsOpen = false;
        }
    }

    private static Popup NewPopup()
    {
        var popup = new Popup { Placement = PlacementMode.Bottom, StaysOpen = true };
        popup.MouseEnter += (_, _) => CloseTimer.Stop();
        popup.MouseLeave += (_, _) => CloseTimer.Start();
        return popup;
    }

    private static Border Content(GitHubPill pill, DataTemplate rowTemplate)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = pill.Heading, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new ItemsControl { ItemsSource = pill.Rows.Take(5).ToList(), ItemTemplate = rowTemplate });
        if (pill.MoreText is { } more)
        {
            panel.Children.Add(new TextBlock { Text = more, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0) });
        }

        var border = new Border
        {
            Width = 440,
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb(0x26, 0x2B, 0x33)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            Child = panel,
        };
        TextElement.SetForeground(border, Brushes.White);
        return border;
    }
}
