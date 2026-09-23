using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Shell;
using CodeSwitchX.UI.Workspaces;

namespace CodeSwitchX.UI;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly HotkeyService _hotkeys;
    private readonly TrayIconService _tray;
    private readonly HostManager _host;
    private readonly Func<AddWorkspaceViewModel> _addWorkspaceFactory;
    private WindowLocationWatcher? _locationWatcher;
    private System.Windows.Threading.DispatcherTimer? _livenessTimer;

    public MainWindow(ShellViewModel shell, HotkeyService hotkeys, TrayIconService tray, HostManager host, Func<AddWorkspaceViewModel> addWorkspaceFactory)
    {
        InitializeComponent();
        _shell = shell;
        _hotkeys = hotkeys;
        _tray = tray;
        _host = host;
        _addWorkspaceFactory = addWorkspaceFactory;
        DataContext = shell;
        CabView.HostRectChanged += rect => _shell.UpdateCabRect(rect);
        shell.Yard.AddWorkspaceRequested += () => _ = ShowAddWorkspaceAsync();
    }

    private async Task ShowAddWorkspaceAsync()
    {
        var viewModel = _addWorkspaceFactory();
        await viewModel.LoadAsync(CancellationToken.None);
        var dialog = new AddWorkspaceWindow(viewModel) { Owner = this };
        dialog.ShowDialog();
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
        _hotkeys.Attach(hwnd, _shell);
        _tray.Attach(this, _shell);
        _locationWatcher = new WindowLocationWatcher();
        _locationWatcher.Moved += movedHwnd => _host.SnapBack(movedHwnd);
        _livenessTimer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(2), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => _host.PollLiveness(), Dispatcher);
        _livenessTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _livenessTimer?.Stop();
        _locationWatcher?.Dispose();
        _hotkeys.Detach();
        _tray.Detach();
        _host.HideAll();
        base.OnClosed(e);
    }
}
