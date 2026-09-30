using System.Diagnostics;
using System.IO;
using CodeSwitchX.Core;
using Path = System.IO.Path;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Data;
using CodeSwitchX.Ingest.Hooks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Settings;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ClaudeHookInstaller _installer;
    private readonly ISettingsStore _settings;
    private readonly PersistenceWriterOptions _writerOptions;
    private readonly ILogger<SettingsViewModel> _logger;
    private bool _loading;
    private readonly Lock _saveGate = new();
    private readonly Dictionary<string, Func<CancellationToken, Task>> _pendingSaves = [];
    private Task _drain = Task.CompletedTask;
    private bool _draining;

    [ObservableProperty] private string _relayExecutable;
    [ObservableProperty] private HookInstallState _hookState;
    [ObservableProperty] private string _hookStatusText = string.Empty;
    [ObservableProperty] private string? _lastMessage;
    [ObservableProperty] private bool _storePayloads;
    [ObservableProperty] private long? _fiveHourBudgetTokens;

    /// <summary>The Yard's tile size as stored; the Yard owns it (see <see cref="Yard.YardViewModel.TileScale"/>).</summary>
    [ObservableProperty] private double _tileScale = 1;

    public SettingsViewModel(ClaudeHookInstaller installer, ISettingsStore settings, PersistenceWriterOptions writerOptions, AppPaths paths, ClaudeCodePaths claude,
        ILogger<SettingsViewModel> logger)
    {
        _installer = installer;
        _settings = settings;
        _writerOptions = writerOptions;
        _logger = logger;
        DataFolder = paths.Root;
        LogsFolder = paths.LogsDirectory;
        SettingsFile = claude.SettingsFile;
        _relayExecutable = DefaultRelayExecutable;
    }

    public static string DefaultRelayExecutable => Path.Combine(AppContext.BaseDirectory, "relay", "csx-hook.exe");

    public string DataFolder { get; }
    public string LogsFolder { get; }
    public string SettingsFile { get; }
    public event Action<long?>? BudgetChanged;
    public event Action? CloseRequested;

    public async Task LoadAsync(CancellationToken ct)
    {
        _loading = true;
        try
        {
            // The startup coordinator read the stored choice into the writer before the first hook event; the view shows
            // the writer's flag rather than reading the row again, which could disagree with what the writer does.
            TileScale = await LoadTileScaleAsync(ct);
            StorePayloads = _writerOptions.StorePayloads;
            FiveHourBudgetTokens = await _settings.GetAsync<long?>(SettingKeys.FiveHourBudgetTokens, ct);
            RelayExecutable = await _settings.GetAsync<string>(SettingKeys.RelayExecutable, ct) ?? DefaultRelayExecutable;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading settings failed");
            LastMessage = ex.Message;
        }
        finally
        {
            _loading = false;
        }

        Refresh();
    }

    public void Refresh()
    {
        var status = _installer.GetStatus(RelayExecutable);
        HookState = status.State;
        HookStatusText = status.State switch
        {
            HookInstallState.Installed => $"Installed ({status.InstalledEvents.Count} of {ClaudeHookInstaller.Events.Length} events)",
            HookInstallState.Partial => $"Partial: missing {string.Join(", ", status.MissingEvents)}. Install again to add {(status.MissingEvents.Count == 1 ? "it" : "them")}.",
            HookInstallState.Outdated => "Installed, but the entries are out of date (an older path or form). Install again to update them.",
            HookInstallState.Unreadable => $"Unknown: {status.Problem}",
            _ => "Not installed. Tile states fall back to transcript inference.",
        };
    }

    /// <summary>Off the UI thread: the installer retries the file replace for about half a second while another process holds settings.json.</summary>
    [RelayCommand]
    private async Task InstallHooksAsync()
    {
        var relay = RelayExecutable;
        try
        {
            var result = await Task.Run(() => _installer.Install(relay));
            LastMessage = result.Changed
                ? $"Hooks written to {SettingsFile}" + (result.BackupFile is null ? string.Empty : $" (backup: {Path.GetFileName(result.BackupFile)})")
                : "Hooks were already up to date.";
        }
        catch (HookInstallException ex)
        {
            LastMessage = ex.Message;
        }

        Refresh();
    }

    [RelayCommand]
    private async Task RemoveHooksAsync()
    {
        try
        {
            var result = await Task.Run(_installer.Uninstall);
            LastMessage = result.Changed ? "CodeSwitchX hooks removed." : "No CodeSwitchX hooks were present.";
        }
        catch (HookInstallException ex)
        {
            LastMessage = ex.Message;
        }

        Refresh();
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(DataFolder);

    [RelayCommand]
    private void OpenLogsFolder() => OpenFolder(LogsFolder);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    partial void OnStorePayloadsChanged(bool value)
    {
        _writerOptions.StorePayloads = value;
        Persist(SettingKeys.StorePayloads, value);
    }

    partial void OnFiveHourBudgetTokensChanged(long? value)
    {
        Persist(SettingKeys.FiveHourBudgetTokens, value);
        BudgetChanged?.Invoke(value);
    }

    /// <summary>
    /// In a try of its own: a stored value that is not a number (edited by hand) leaves the tiles at their normal size
    /// without a message in the Settings view, and a failure reading the other settings does not lose the size.
    /// </summary>
    private async Task<double> LoadTileScaleAsync(CancellationToken ct)
    {
        try
        {
            return await _settings.GetAsync<double?>(SettingKeys.TileScale, ct) ?? 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reading the tile size failed; the tiles keep their normal size");
            return 1;
        }
    }

    /// <summary>A drag of the slider changes the value many times; the save queue keeps only the latest.</summary>
    partial void OnTileScaleChanged(double value) => Persist(SettingKeys.TileScale, value);

    partial void OnRelayExecutableChanged(string value)
    {
        Persist(SettingKeys.RelayExecutable, value);
        Refresh();
    }

    /// <summary>How long one save may take. A write waiting out SQLite's busy timeout must not hold every later save back.</summary>
    internal TimeSpan SaveTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Waits for the saves queued so far. The app's exit calls this before the host stops, so a value changed right
    /// before the exit is in the database at the next start, the way the view and the writer already had it.
    /// </summary>
    public async Task FlushSavesAsync(CancellationToken ct)
    {
        Task drain;
        lock (_saveGate)
        {
            drain = _drain;
        }

        await drain.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves run one after the other on one queue, and the queue holds the latest value per key: a change while the
    /// key's save runs is saved after it, and changes behind that collapse into the latest, so the value stored last is
    /// the one the view shows.
    /// </summary>
    private void Persist<T>(string key, T value)
    {
        if (_loading)
        {
            return;
        }

        lock (_saveGate)
        {
            _pendingSaves[key] = ct => _settings.SetAsync(key, value, ct);
            if (!_draining)
            {
                _draining = true;
                _drain = Task.Run(DrainSavesAsync);
            }
        }
    }

    private async Task DrainSavesAsync()
    {
        while (true)
        {
            string key;
            Func<CancellationToken, Task> save;
            lock (_saveGate)
            {
                if (_pendingSaves.Count == 0)
                {
                    _draining = false;
                    return;
                }

                (key, save) = _pendingSaves.First();
                _pendingSaves.Remove(key);
            }

            try
            {
                using var timeout = new CancellationTokenSource(SaveTimeout);
                // WaitAsync as well: a store call that ignores the token is given up too, not waited for.
                await save(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving setting {Key} failed", key);
            }
        }
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
        }
    }
}
