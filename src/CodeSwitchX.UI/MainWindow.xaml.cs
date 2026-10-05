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
    private bool _raiseHostedWhenMoved;

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
        shell.ForwardRequested += () => WindowActivation.BringUp(this);
        shell.Window = this; // Raven minimizes and maximizes it by voice (#117)
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

    bool IShellWindow.IsInFront => Win32WindowEnumerator.Foreground() is var front && front != 0
        && (front == _hwnd || front == _host.ShownInCab);

    /// <summary>As the title bar's button: StateChanged tells the shell, which hides the Cab's VS Code window with it.</summary>
    void IShellWindow.Minimize() => WindowState = WindowState.Minimized;

    void IShellWindow.Show(ShellWindowState state)
    {
        WindowState = state == ShellWindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        WindowActivation.BringUp(this);
    }

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
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized)
            {
                _restored = WindowState == WindowState.Maximized ? ShellWindowState.Maximized : ShellWindowState.Normal;
            }

            _shell.SetShellMinimized(WindowState == WindowState.Minimized);
        };
    }

    /// <summary>
    /// An activation of the shell does not lift it over the VS Code window its Cab shows, while the two are the front
    /// windows (<see cref="ZOrder.KeepUnder"/>): VS Code vanished for a moment on every click on the shell (#82). From
    /// behind another app the shell does go over VS Code, and VS Code is raised again as soon as the move is done, in
    /// the same message rather than a dispatcher pass later.
    /// </summary>
    private nint StayUnderHostedWindow(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmWindowPosChanging)
        {
            _raiseHostedWhenMoved = ZOrder.KeepUnder(lParam, hwnd, _host.ShownInCab) == FrontMove.Lifted;
        }
        else if (msg == WmWindowPosChanged && _raiseHostedWhenMoved)
        {
            _raiseHostedWhenMoved = false;
            _shell.RaiseHostedWindow(focus: false);
        }

        return 0;
    }

    /// <summary>
    /// Activating the shell from behind other windows raises it above the docked VS Code window; put VS Code back on top
    /// while in Cab mode. Activated by the keyboard (Alt+Tab, the tray), VS Code takes the foreground too. A click that
    /// activated the shell must keep the foreground until it is released: VS Code, given the foreground while the button
    /// was still down, took the mouse from the shell's button, which then never clicked (← Yard did nothing while VS Code
    /// had the focus). So VS Code goes back on top at once without the foreground. A click on the shell's content keeps
    /// the foreground in the shell: the Raven panel's controls need the keyboard, and a list opened by the click would close
    /// again as the shell lost it (#82). A click on the title bar, a drag of it included, hands it to VS Code on the
    /// release, unless the click left the Cab.
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
            if (!ZOrder.IsFrontPair(_host.ShownInCab, _hwnd))
            {
                _shell.RaiseHostedWindow(focus: false);
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
