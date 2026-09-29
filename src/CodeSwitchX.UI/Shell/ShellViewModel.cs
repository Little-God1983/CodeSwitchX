using CodeSwitchX.Core;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.Ingest.Hooks;
using CodeSwitchX.UI.Cab;
using CodeSwitchX.UI.Settings;
using CodeSwitchX.UI.Telemetry;
using CodeSwitchX.UI.Yard;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Shell;

/// <summary>Owns the Yard/Cab/Settings mode switch and drives the HostManager for the active workspace.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly HostManager _host;
    private readonly ILogger<ShellViewModel> _logger;

    [ObservableProperty]
    private ShellMode _mode = ShellMode.Yard;

    [ObservableProperty]
    private Guid? _activeWorkspaceId;

    [ObservableProperty]
    private string? _statusMessage;

    private bool _shellMinimized;

    /// <summary>Counts the opens; the status strip belongs to the latest one (see <see cref="ReportFor"/>).</summary>
    private int _openAttempt;

    public ShellViewModel(YardViewModel yard, CabViewModel cab, SettingsViewModel settings, PerformanceBarViewModel performanceBar,
        HostManager host, ILogger<ShellViewModel> logger)
    {
        Yard = yard;
        Cab = cab;
        Settings = settings;
        PerformanceBar = performanceBar;
        _host = host;
        _logger = logger;
        Yard.OpenRequested += id => _ = EnterCabAsync(id);
        Yard.TileRemoved += OnTileRemoved;
        Yard.HostStopped += OnHostStopped;
        Cab.BackRequested += BackToYard;
        Cab.SwitchRequested += id => _ = EnterCabAsync(id);
    }

    /// <summary>The window title: the only place on screen that tells a stable build from a Debug build of the same version.</summary>
    public string Title { get; } = $"CodeSwitchX {AppVersion.Display}";

    public YardViewModel Yard { get; }
    public CabViewModel Cab { get; }
    public SettingsViewModel Settings { get; }
    public PerformanceBarViewModel PerformanceBar { get; }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await Yard.InitializeAsync(ct);
        await PerformanceBar.InitializeAsync(ct);
        await Settings.LoadAsync(ct);
        Settings.BudgetChanged += PerformanceBar.SetBudget;
        Settings.CloseRequested += CloseSettings;
        // The Yard's "Hooks not installed" banner follows the installer, so it goes when Install hooks is clicked.
        Yard.HooksInstalled = HooksReachUs(Settings.HookState);
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.HookState))
            {
                Yard.HooksInstalled = HooksReachUs(Settings.HookState);
            }
        };
        _ = AutoStartAsync();
    }

    /// <summary>Installed, or Partial (every installed event reaches us); Outdated entries point elsewhere and reach nobody.</summary>
    private static bool HooksReachUs(HookInstallState state) => state is HookInstallState.Installed or HookInstallState.Partial;

    /// <summary>
    /// The Cab cannot show a workspace that is gone: its pip goes whichever workspace is active; when it is the active one,
    /// the strip's name, the pips and the jump target are cleared, an open of it still running reports nowhere, and the Yard is shown.
    /// </summary>
    private void OnTileRemoved(Guid workspaceId)
    {
        Cab.RemovePip(workspaceId);
        if (ActiveWorkspaceId != workspaceId)
        {
            return;
        }

        _openAttempt++;
        ActiveWorkspaceId = null;
        StatusMessage = null;
        Cab.Clear();
        if (Mode == ShellMode.Cab)
        {
            BackToYard();
        }
    }

    /// <summary>
    /// The VS Code the Cab shows was closed (its X button, File > Close Window): the Cab would stay empty, so the Yard is
    /// shown. A workspace that is not active, or an open that never got a window, leaves the Cab alone.
    /// </summary>
    private void OnHostStopped(Guid workspaceId)
    {
        if (Mode == ShellMode.Cab && ActiveWorkspaceId == workspaceId)
        {
            BackToYard();
        }
    }

    /// <summary>"Start with CodeSwitchX": launch those workspaces now; their windows stay cloaked until a tile is opened.</summary>
    private async Task AutoStartAsync()
    {
        foreach (var tile in Yard.Tiles.Where(t => t.Workspace.AutoStart).ToList())
        {
            try
            {
                await _host.OpenAsync(tile.Workspace, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-start of {Workspace} failed", tile.Name);
            }
        }
    }

    [RelayCommand]
    public async Task EnterCabAsync(Guid workspaceId)
    {
        var tile = Yard.FindTile(workspaceId);
        if (tile is null)
        {
            return;
        }

        if (ActiveWorkspaceId != workspaceId)
        {
            // The strip names the new workspace from here on; the VS Code shown so far must not stay in the Cab while
            // the new one starts, or for good when it does not.
            _host.HideAll();
        }

        ActiveWorkspaceId = workspaceId;
        Cab.SetActive(tile, Yard.Tiles);
        Mode = ShellMode.Cab;
        StatusMessage = null;
        var attempt = ++_openAttempt;

        try
        {
            var hosted = await _host.OpenAsync(tile.Workspace, CancellationToken.None);
            if (hosted.State != HostState.Running)
            {
                ReportFor(attempt, hosted.Error ?? "VS Code did not start.");
                return;
            }

            if (ActiveWorkspaceId == workspaceId)
            {
                RaiseHostedWindow();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening workspace {Workspace} failed", tile.Name);
            ReportFor(attempt, ex.Message);
        }
    }

    /// <summary>
    /// The status strip belongs to the latest open: one the user has moved on from, to another workspace or to a retry of
    /// this one, does not report there. Which workspace is active cannot tell a retry apart from the open before it.
    /// </summary>
    private void ReportFor(int attempt, string message)
    {
        if (attempt == _openAttempt)
        {
            StatusMessage = message;
        }
    }

    [RelayCommand]
    public void BackToYard()
    {
        _host.HideAll();
        Mode = ShellMode.Yard;
    }

    [RelayCommand]
    public void ToggleMode()
    {
        if (Mode == ShellMode.Cab)
        {
            BackToYard();
        }
        else if (ActiveWorkspaceId is { } id)
        {
            _ = EnterCabAsync(id);
        }
    }

    [RelayCommand]
    public async Task JumpToAsync(int oneBasedIndex)
    {
        var tiles = Yard.Tiles.ToList();
        if (oneBasedIndex < 1 || oneBasedIndex > tiles.Count)
        {
            return;
        }

        await EnterCabAsync(tiles[oneBasedIndex - 1].Id);
    }

    [RelayCommand]
    public void OpenSettings()
    {
        _host.HideAll();
        Settings.Refresh();
        Mode = ShellMode.Settings;
    }

    [RelayCommand]
    public void CloseSettings() => Mode = ShellMode.Yard;

    /// <summary>
    /// Called by the window when it is minimised or restored. A minimised shell reports an off-screen host rectangle;
    /// docking VS Code there would leave an invisible window holding keyboard focus, so it is hidden instead and
    /// docked again on restore.
    /// </summary>
    public void SetShellMinimized(bool minimized)
    {
        if (_shellMinimized == minimized)
        {
            return;
        }

        _shellMinimized = minimized;
        if (Mode != ShellMode.Cab)
        {
            return;
        }

        if (minimized)
        {
            _host.HideAll();
        }
        else
        {
            RaiseHostedWindow();
        }
    }

    /// <summary>
    /// Shows the active workspace's VS Code in the Cab and raises it, when the shell can show it: in Cab mode, not
    /// minimised, with a known Cab rectangle. The one place for that rule: an open that finishes, a restore, and an
    /// activation of the shell (which puts the shell above the docked VS Code) all come here.
    /// </summary>
    public void RaiseHostedWindow() => RaiseHostedWindow(focus: true);

    /// <param name="focus">False puts VS Code on top without the foreground: a click on the shell that activated it is still going on.</param>
    public void RaiseHostedWindow(bool focus)
    {
        if (!_shellMinimized && Mode == ShellMode.Cab && ActiveWorkspaceId is { } id && Cab.LastHostRect is { } rect)
        {
            _host.ShowInCab(id, rect, focus);
        }
    }

    /// <summary>Called by the Cab view whenever the host area's screen rectangle changes: a move only, never a raise.</summary>
    public void UpdateCabRect(ScreenRect rect)
    {
        if (_shellMinimized)
        {
            return; // a minimised window measures at roughly (-32000, -32000)
        }

        Cab.LastHostRect = rect;
        if (Mode == ShellMode.Cab && ActiveWorkspaceId is { } id)
        {
            _host.Dock(id, rect);
        }
    }
}
