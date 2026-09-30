using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using CodeSwitchX.Conductor;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Shell;
using CodeSwitchX.UI.Workspaces;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly HotkeyService _hotkeys;
    private readonly TrayIconService _tray;
    private readonly HostManager _host;
    private readonly IAgentLauncher _agents;
    private readonly AddWorkspaceLauncher _addWorkspace;
    private readonly ILogger<WindowLocationWatcher> _watcherLogger;
    private WindowLocationWatcher? _locationWatcher;
    private MouseBackButtonHook? _backButtonHook;
    private System.Windows.Threading.DispatcherTimer? _livenessTimer;
    private System.Windows.Threading.DispatcherTimer? _focusOnRelease;

    public MainWindow(ShellViewModel shell, HotkeyService hotkeys, TrayIconService tray, HostManager host, IAgentLauncher agents,
        Func<AddWorkspaceViewModel> addWorkspaceFactory, ILogger<AddWorkspaceLauncher> addWorkspaceLogger, ILogger<WindowLocationWatcher> watcherLogger)
    {
        InitializeComponent();
        _shell = shell;
        _hotkeys = hotkeys;
        _tray = tray;
        _host = host;
        _agents = agents;
        _addWorkspace = new AddWorkspaceLauncher(addWorkspaceFactory, ShowAddWorkspaceDialog, addWorkspaceLogger);
        _watcherLogger = watcherLogger;
        DataContext = shell;
        CabView.HostRectChanged += rect => _shell.UpdateCabRect(rect);
        shell.Yard.AddWorkspaceRequested += path => _ = _addWorkspace.OpenAsync(path);
        shell.ForwardRequested += () => WindowActivation.BringUp(this);
    }

    /// <summary>
    /// The chats Raven started run in processes of this app, which end with it: while one is working, closing asks first,
    /// and naming them. A chat between turns just ends; VS Code can open it later. The tray's Exit closes this window, so
    /// it asks too.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        var working = _agents.Chats.Where(c => c.Working).ToList();
        if (working.Count > 0)
        {
            var which = string.Join(", ", working.Select(c => c.Workspace).Distinct());
            var message = working.Count == 1
                ? $"A chat Raven started in {which} is still working. Closing CodeSwitchX stops it, in the middle of its work.\n\nClose anyway?"
                : $"{working.Count} chats Raven started ({which}) are still working. Closing CodeSwitchX stops them, in the middle of their work.\n\nClose anyway?";
            if (MessageBox.Show(this, message, "CodeSwitchX", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        base.OnClosing(e);
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
        HwndSource.FromHwnd(hwnd)?.AddHook(TimeZoneRefresh.WndProc);
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
        StateChanged += (_, _) => _shell.SetShellMinimized(WindowState == WindowState.Minimized);
    }

    /// <summary>
    /// Activating the shell raises it above the docked VS Code window; put VS Code back on top while in Cab mode. A click
    /// that activated the shell must keep the foreground until it is released: VS Code, given the foreground while the
    /// button was still down, took the mouse from the shell's button, which then never clicked (← Yard did nothing while
    /// VS Code had the focus). So VS Code goes back on top at once without the foreground, and takes it on the release,
    /// unless the click left the Cab. A drag of the shell's title bar is such a click too.
    /// </summary>
    private void OnActivated(object? sender, EventArgs e)
    {
        if (!WindowActivation.AnyMouseButtonDown())
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => _shell.RaiseHostedWindow());
            return;
        }

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => _shell.RaiseHostedWindow(focus: false));
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
