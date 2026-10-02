using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace CodeSwitchX.UI.Raven;

public partial class RavenPanelView : UserControl
{
    private RavenPanelViewModel? _viewModel;

    private readonly MicModeSwitch _modeSwitch;

    /// <summary>The mic button the mouse is holding down, if any.</summary>
    private Button? _heldMic;

    public RavenPanelView()
    {
        _modeSwitch = new MicModeSwitch(() => _viewModel);
        InitializeComponent();
        DataContextChanged += (_, _) => Bind(DataContext as RavenPanelViewModel);
    }

    private void Bind(RavenPanelViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.Log.CollectionChanged -= OnLogChanged;
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.Log.CollectionChanged += OnLogChanged;
            _viewModel.PropertyChanged += OnViewModelChanged;
        }
    }

    /// <summary>Lines added while the panel was collapsed scrolled a log nobody saw: show the newest once it is open again.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RavenPanelViewModel.IsOpen) && _viewModel is { IsOpen: true })
        {
            ScrollLogToEnd();
        }
    }

    /// <summary>The newest line is the one to read: scroll to it once the new item has been laid out.</summary>
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            ScrollLogToEnd();
        }
    }

    /// <summary>Once the layout has caught up with the change.</summary>
    private void ScrollLogToEnd() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => LogScroller.ScrollToEnd());

    /// <summary>
    /// A reply grows in place as it streams in, which adds no entry: the log follows it while its end was in view before
    /// it grew, and stays put when the user has scrolled up to read.
    /// </summary>
    private void OnLogScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - e.ExtentHeightChange - 1)
        {
            LogScroller.ScrollToEnd();
        }
    }

    /// <summary>
    /// Press on mouse-down, release on mouse-up. The mouse is captured, so a hold that drifts off the button still ends
    /// with a release; a capture taken away (another window comes up mid-hold) counts as a release too.
    /// </summary>
    private void OnMicDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null || sender is not Button button)
        {
            return;
        }

        e.Handled = true;
        button.Focus();
        _heldMic = button;
        button.CaptureMouse();
        _viewModel.PressMic(TalkInput.MicButton);
    }

    private void OnMicUp(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(sender, _heldMic))
        {
            return;
        }

        e.Handled = true;
        ReleaseHeldMic();
    }

    private void OnMicCaptureLost(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(sender, _heldMic))
        {
            ReleaseHeldMic();
        }
    }

    private void ReleaseHeldMic()
    {
        var button = _heldMic;
        _heldMic = null; // first: giving up the capture raises LostMouseCapture, which must not release twice
        button?.ReleaseMouseCapture();
        if (_viewModel is not null)
        {
            _ = _viewModel.ReleaseMicAsync(TalkInput.MicButton);
        }
    }

    /// <summary>Space or Enter on the focused mic works as a tap: the first starts listening, the next stops.</summary>
    private void OnMicKey(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter) || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        e.Handled = true;
        if (e.IsRepeat || _heldMic is not null || _viewModel is null)
        {
            return;
        }

        _ = _viewModel.TapMic(TalkInput.MicButton);
    }

    private void OnModeChecked(object sender, RoutedEventArgs e) => _modeSwitch.OnChecked(sender);

    private void OnModeClick(object sender, RoutedEventArgs e) => _modeSwitch.OnClicked(sender);
}
