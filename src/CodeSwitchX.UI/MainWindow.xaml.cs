using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Shell;
using CodeSwitchX.UI.Workspaces;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI;

public partial class MainWindow : Window, IShellWindow
{
    private readonly ShellViewModel _shell;
    private readonly HotkeyService _hotkeys;
    private readonly TrayIconService _tray;
    private readonly HostManager _host;
    private readonly AddWorkspaceLauncher _addWorkspace;
    private readonly ILogger<WindowLocationWatcher> _watcherLogger;
    private WindowLocationWatcher? _locationWatcher;
    private MouseBackButtonHook? _backButtonHook;
    private System.Windows.Threading.DispatcherTimer? _livenessTimer;
    private System.Windows.Threading.DispatcherTimer? _focusOnRelease;
    private nint _hwnd;
    private const int WmWindowPosChanging = 0x0046;
    private const int WmWindowPosChanged = 0x0047;
    private bool _tuckUnderHostedWhenMoved;

    public MainWindow(ShellViewModel shell, HotkeyService hotkeys, TrayIconService tray, HostManager host,
        Func<AddWorkspaceViewModel> addWorkspaceFactory, ILogger<AddWorkspaceLauncher> addWorkspaceLogger, ILogger<WindowLocationWatcher> watcherLogger)
    {
        InitializeComponent();
        _shell = shell;
        _hotkeys = hotkeys;
        _tray = tray;
        _host = host;
        _addWorkspace = new AddWorkspaceLauncher(addWorkspaceFactory, ShowAddWorkspaceDialog, addWorkspaceLogger);
        _watcherLogger = watcherLogger;
        DataContext = shell;
        CabView.HostRectChanged += rect => _shell.UpdateCabRect(rect);
        shell.Yard.AddWorkspaceRequested += path => _ = _addWorkspace.OpenAsync(path);
        // What Raven opens by voice comes to the front with the keyboard, past Windows' foreground lock (#224).
        shell.ForwardRequested += () => BringForwardAsAsked(opening: true);
        shell.AskBeforeRemove = (workspace, cards) => Task.FromResult(RemoveWorkspaceWindow.Ask(this, workspace, cards));
        // The first use of the Raven panel: open at the start, Settings → Voice opens once the window shows.
        ContentRendered += (_, _) => shell.OfferVoiceSetup();
    }

    ShellWindowState IShellWindow.State => WindowState switch
    {
        WindowState.Minimized => ShellWindowState.Minimized,
        WindowState.Maximized => ShellWindowState.Maximized,
        _ => ShellWindowState.Normal,
    };

    ShellWindowState IShellWindow.Restored => _restored;

    /// <summary>The state before the last minimize, kept as the window changes: "bring it back" returns a maximized window maximized.</summary>
    private ShellWindowState _restored = ShellWindowState.Normal;

    /// <summary>It, a dialog of it (Add workspace), or the VS Code window its Cab shows, which holds the focus there.</summary>
    bool IShellWindow.IsInFront => Win32WindowEnumerator.Foreground() is var front && front != 0
        && (front == _host.ShownInCab || Application.Current.Windows.OfType<Window>().Any(w => new WindowInteropHelper(w).Handle == front));

    /// <summary>As the title bar's button: StateChanged tells the shell, which hides the Cab's VS Code window with it.</summary>
    void IShellWindow.Minimize() => WindowState = WindowState.Minimized;

    void IShellWindow.Show(ShellWindowState state)
    {
        WindowState = state == ShellWindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        BringForwardAsAsked(opening: false);
    }

    /// <param name="opening">Raven opens something (a workspace, Settings): what the Cab shows changes, so the shell takes the
    /// keyboard also from the Cab's VS Code, and its activation hands it to the VS Code shown then, if any.</param>
    private void BringForwardAsAsked(bool opening)
    {
        WindowActivation.BringUp(this);
        TakeForegroundAsAsked(opening);
    }

    /// <summary>
    /// Asked by voice (set_window, and what Raven opens), the request is no input to this process, and Windows' foreground
    /// lock keeps the app in front where it is: the user asked for CodeSwitchX, so it takes the foreground past it, keyboard
    /// and all (#222, #224). Not while a mouse button is held: the activation would be read as a click on the shell; nor
    /// while a modifier is, whose release would land here and leave it held in the other app. Not while the Cab's VS Code
    /// has the focus either, unless Raven opens something there: CodeSwitchX is in front then, and taking it would move the
    /// keyboard off VS Code. The shell still goes right under that VS Code, over whatever covered it, so VS Code does not
    /// stand alone over the app the user was in.
    /// </summary>
    private void TakeForegroundAsAsked(bool opening)
    {
        if (WindowActivation.AnyMouseButtonDown() || WindowActivation.AnyModifierDown())
        {
            return;
        }

        var front = Win32WindowEnumerator.Foreground();
        if (Application.Current.Windows.OfType<Window>().Any(w => new WindowInteropHelper(w).Handle == front))
        {
            return; // a window of its own has the keyboard already
        }

        if (!opening && _host.ShownInCab is var shown and not 0 && front == shown)
        {
            if (!ZOrder.IsFrontPair(shown, _hwnd))
            {
                ZOrder.TuckUnder(_hwnd, shown);
            }

            return;
        }

        ForegroundLock.Take(_hwnd);
    }

    nint IShellWindow.Dialog =>
        OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsVisible) is { } dialog ? new WindowInteropHelper(dialog).Handle : 0;

    /// <summary>Shows the Add workspace dialog over the shell until it is closed.</summary>
    private Task ShowAddWorkspaceDialog(AddWorkspaceViewModel viewModel)
    {
        var dialog = new AddWorkspaceWindow(viewModel) { Owner = this };
        if (viewModel.InputPath.Length > 0)
        {
            // A dropped path: the drag came from another window, Explorer say, which still has the focus.
            Activate();
        }

        dialog.ShowDialog();
        return Task.CompletedTask;
    }

    /// <summary>Mouse "back" button (XButton1) returns to the Yard, as the spec asks.</summary>
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1 && _shell.Mode == ShellMode.Cab)
        {
            _shell.BackToYard();
            e.Handled = true;
        }

        base.OnPreviewMouseDown(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        _hwnd = hwnd;
        _shell.Window = this; // Raven minimizes and maximizes it by voice (#117), once it is a window
        HwndSource.FromHwnd(hwnd)?.AddHook(TimeZoneRefresh.WndProc);
        HwndSource.FromHwnd(hwnd)?.AddHook(StayUnderHostedWindow);
        _hotkeys.Attach(hwnd, _shell);
        foreach (var binding in _hotkeys.FailedBindings)
        {
            if (HotkeyService.RavenFailureNote(binding) is { } note)
            {
                _shell.Raven.Note(note);
            }
        }

        _tray.Attach(this, _shell);
        _locationWatcher = new WindowLocationWatcher(_watcherLogger);
        _locationWatcher.Moved += movedHwnd => _host.SnapBack(movedHwnd);
        _locationWatcher.MoveSizeStarted += draggedHwnd => _host.RefuseMoveSize(draggedHwnd);
        _locationWatcher.Destroyed += destroyedHwnd => _host.WindowDestroyed(destroyedHwnd);
        _locationWatcher.Appeared += newHwnd => _host.WindowAppeared(newHwnd);
        _backButtonHook = new MouseBackButtonHook(_host.IsShownInCab,
            () => Dispatcher.BeginInvoke(() => { if (_shell.Mode == ShellMode.Cab) { _shell.BackToYard(); } }), _watcherLogger);
        // Only while the Cab shows a window: the rest of the time the hook would hold every mouse event for nothing.
        _host.ShownInCabChanged += shownHwnd => _backButtonHook?.SetActive(shownHwnd != 0);
        _livenessTimer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(2), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => _host.PollLiveness(), Dispatcher);
        _livenessTimer.Start();
        Activated += OnActivated;
        // The content's width, not the window's: what folds on a narrow window is reckoned from the room inside it (#162).
        if (Content is FrameworkElement content)
        {
            content.SizeChanged += (_, e) =>
            {
                if (e.WidthChanged)
                {
                    _shell.SetWidth(e.NewSize.Width);
                }
            };

            // Laid out already, its first change is past.
            if (content.ActualWidth > 0)
            {
                _shell.SetWidth(content.ActualWidth);
            }
        }

        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized)
            {
                _restored = ((IShellWindow)this).State;
            }

            _shell.SetShellMinimized(WindowState == WindowState.Minimized);
        };
    }

    /// <summary>
    /// An activation of the shell does not lift it over the VS Code window its Cab shows, while the two are the front
    /// windows (<see cref="ZOrder.KeepUnder"/>): VS Code vanished for a moment on every click on the shell (#82). From
    /// behind another app's window the shell does go over VS Code, and puts itself back under it as soon as the move is
    /// done, in the same message rather than a dispatcher pass later; that window is lowered under both
    /// (<see cref="ZOrder.TuckUnder"/>). Raising VS Code instead left it buried: Windows ignores that raise (#213).
    /// </summary>
    private nint StayUnderHostedWindow(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmWindowPosChanging)
        {
            _tuckUnderHostedWhenMoved = ZOrder.KeepUnder(lParam, hwnd, _host.ShownInCab) == FrontMove.Lifted;
        }
        else if (msg == WmWindowPosChanged && _tuckUnderHostedWhenMoved)
        {
            _tuckUnderHostedWhenMoved = false;
            ZOrder.TuckUnder(hwnd, _host.ShownInCab);
        }

        return 0;
    }

    /// <summary>
    /// Activating the shell from behind other windows raises it above the docked VS Code window; put VS Code back on top
    /// while in Cab mode. Activated by the keyboard (Alt+Tab, the tray), VS Code takes the foreground too. A click that
    /// activated the shell must keep the foreground until it is released: VS Code, given the foreground while the button
    /// was still down, took the mouse from the shell's button, which then never clicked (← Yard did nothing while VS Code
    /// had the focus). So the shell goes back under VS Code at once and keeps the foreground (VS Code itself cannot be
    /// raised over it then, #213). A click on the shell's content keeps the foreground in the shell: the Raven panel's
    /// controls need the keyboard, and a list opened by the click would close again as the shell lost it (#82). A click on
    /// the title bar, a drag of it included, hands it to VS Code on the release, unless the click left the Cab.
    /// </summary>
    private void OnActivated(object? sender, EventArgs e)
    {
        if (!WindowActivation.AnyMouseButtonDown())
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => _shell.RaiseHostedWindow());
            return;
        }

        var onContent = ZOrder.CursorInClientArea(_hwnd);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (ZOrder.IsFrontPair(_host.ShownInCab, _hwnd))
            {
                return;
            }

            // A Cab whose VS Code is not shown yet shows it first, without the foreground; that raise leaves it under the shell.
            if (_host.ShownInCab == 0)
            {
                _shell.RaiseHostedWindow(focus: false);
            }

            if (_host.ShownInCab is var shown and not 0)
            {
                ZOrder.TuckUnder(_hwnd, shown);
            }
        });
        if (onContent)
        {
            return;
        }

        if (_focusOnRelease is null)
        {
            _focusOnRelease = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(30),
            };
            _focusOnRelease.Tick += FocusOnRelease;
        }

        _focusOnRelease.Start();
    }

    /// <summary>Background priority: the release's own click, queued as input, has run by the time this does.</summary>
    private void FocusOnRelease(object? sender, EventArgs e)
    {
        if (WindowActivation.AnyMouseButtonDown())
        {
            return;
        }

        _focusOnRelease!.Stop();
        _shell.RaiseHostedWindow();
    }

    protected override void OnClosed(EventArgs e)
    {
        _livenessTimer?.Stop();
        _focusOnRelease?.Stop();
        _locationWatcher?.Dispose();
        _backButtonHook?.Dispose();
        _hotkeys.Detach();
        _tray.Detach();
        // DWM cloaking outlives this process: give every hosted VS Code window back to the desktop.
        _host.ReleaseAll();
        base.OnClosed(e);
    }
}
