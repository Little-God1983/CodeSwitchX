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

    [ObservableProperty] private string _relayExecutable;
    [ObservableProperty] private HookInstallState _hookState;
    [ObservableProperty] private string _hookStatusText = string.Empty;
    [ObservableProperty] private string? _lastMessage;
    [ObservableProperty] private bool _storePayloads;
    [ObservableProperty] private long? _fiveHourBudgetTokens;

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
            StorePayloads = await _settings.GetAsync<bool?>(SettingKeys.StorePayloads, ct) ?? false;
            _writerOptions.StorePayloads = StorePayloads;
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
            HookInstallState.Partial => $"Partial: missing {string.Join(", ", status.MissingEvents)}",
            HookInstallState.Outdated => "Installed, but pointing at a different csx-hook.exe. Reinstall to update the path.",
            _ => "Not installed. Tile states fall back to transcript inference.",
        };
    }

    [RelayCommand]
    private void InstallHooks()
    {
        try
        {
            var result = _installer.Install(RelayExecutable);
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
    private void RemoveHooks()
    {
        try
        {
            var result = _installer.Uninstall();
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

    partial void OnRelayExecutableChanged(string value)
    {
        Persist(SettingKeys.RelayExecutable, value);
        Refresh();
    }

    private void Persist<T>(string key, T value)
    {
        if (_loading)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _settings.SetAsync(key, value, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving setting {Key} failed", key);
            }
        });
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
