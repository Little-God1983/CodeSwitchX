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

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly HotkeyService _hotkeys;
    private readonly TrayIconService _tray;
    private readonly HostManager _host;
    private readonly Func<AddWorkspaceViewModel> _addWorkspaceFactory;
    private readonly ILogger<WindowLocationWatcher> _watcherLogger;
    private WindowLocationWatcher? _locationWatcher;
    private System.Windows.Threading.DispatcherTimer? _livenessTimer;
    private bool _addWorkspaceOpen;

    public MainWindow(ShellViewModel shell, HotkeyService hotkeys, TrayIconService tray, HostManager host, Func<AddWorkspaceViewModel> addWorkspaceFactory,
        ILogger<WindowLocationWatcher> watcherLogger)
    {
        InitializeComponent();
        _shell = shell;
        _hotkeys = hotkeys;
        _tray = tray;
        _host = host;
        _addWorkspaceFactory = addWorkspaceFactory;
        _watcherLogger = watcherLogger;
        DataContext = shell;
        CabView.HostRectChanged += rect => _shell.UpdateCabRect(rect);
        shell.Yard.AddWorkspaceRequested += path => _ = ShowAddWorkspaceAsync(path);
    }

    /// <summary>
    /// Opens the Add workspace dialog, detecting <paramref name="path"/> at once when one was dropped on the Yard. One dialog
    /// at a time: the dialog is modal, but a drop or a click can arrive while the tracks load, before it is shown.
    /// </summary>
    private async Task ShowAddWorkspaceAsync(string? path)
    {
        if (_addWorkspaceOpen)
        {
            return;
        }

        _addWorkspaceOpen = true;
        try
        {
            var viewModel = _addWorkspaceFactory();
            await viewModel.LoadAsync(CancellationToken.None);
            var dialog = new AddWorkspaceWindow(viewModel) { Owner = this };
            if (path is not null)
            {
                viewModel.InputPath = path;
                viewModel.ProbeCommand.Execute(null);
                // The drag came from another window, Explorer say, which still has the focus.
                Activate();
            }

            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            // Fire-and-forget from the Yard's button: a failed track load left the dialog unopened without a line anywhere.
            Serilog.Log.Error(ex, "Opening the Add workspace dialog failed");
        }
        finally
        {
            _addWorkspaceOpen = false;
        }
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
        _tray.Attach(this, _shell);
        _locationWatcher = new WindowLocationWatcher(_watcherLogger);
        _locationWatcher.Moved += movedHwnd => _host.SnapBack(movedHwnd);
        _locationWatcher.MoveSizeStarted += draggedHwnd => _host.RefuseMoveSize(draggedHwnd);
        _livenessTimer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(2), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => _host.PollLiveness(), Dispatcher);
        _livenessTimer.Start();
        Activated += OnActivated;
        StateChanged += (_, _) => _shell.SetShellMinimized(WindowState == WindowState.Minimized);
    }

    /// <summary>Activating the shell raises it above the docked VS Code window; put VS Code back on top while in Cab mode.</summary>
    private void OnActivated(object? sender, EventArgs e)
    {
        // Let the click that activated us finish first (e.g. a pip button), then put VS Code back on top.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, _shell.RaiseHostedWindow);
    }

    protected override void OnClosed(EventArgs e)
    {
        _livenessTimer?.Stop();
        _locationWatcher?.Dispose();
        _hotkeys.Detach();
        _tray.Detach();
        // DWM cloaking outlives this process: give every hosted VS Code window back to the desktop.
        _host.ReleaseAll();
        base.OnClosed(e);
    }
}
