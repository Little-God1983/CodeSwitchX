using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.Win32;
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
        Cab.BackRequested += BackToYard;
        Cab.SwitchRequested += id => _ = EnterCabAsync(id);
    }

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
        _ = AutoStartAsync();
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

        ActiveWorkspaceId = workspaceId;
        Cab.SetActive(tile, Yard.Tiles);
        Mode = ShellMode.Cab;
        StatusMessage = null;

        try
        {
            var hosted = await _host.OpenAsync(tile.Workspace, CancellationToken.None);
            if (hosted.State != HostState.Running)
            {
                StatusMessage = hosted.Error ?? "VS Code did not start.";
                return;
            }

            if (Cab.LastHostRect is { } rect && Mode == ShellMode.Cab && ActiveWorkspaceId == workspaceId)
            {
                _host.ShowInCab(workspaceId, rect);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening workspace {Workspace} failed", tile.Name);
            StatusMessage = ex.Message;
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

    /// <summary>Called by the Cab view whenever the host area's screen rectangle changes.</summary>
    public void UpdateCabRect(ScreenRect rect)
    {
        Cab.LastHostRect = rect;
        if (Mode == ShellMode.Cab && ActiveWorkspaceId is { } id)
        {
            _host.ShowInCab(id, rect);
        }
    }
}
