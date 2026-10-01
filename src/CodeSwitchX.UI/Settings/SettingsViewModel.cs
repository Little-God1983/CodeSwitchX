using System.Diagnostics;
using System.IO;
using CodeSwitchX.Conductor;
using CodeSwitchX.Core;
using Path = System.IO.Path;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Data;
using CodeSwitchX.Ingest.Hooks;
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Speech;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Settings;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ClaudeHookInstaller _installer;
    private readonly ISettingsStore _settings;
    private readonly PersistenceWriterOptions _writerOptions;
    private readonly BrainSettings _brain;
    private readonly SpeechSettings _speech;
    private readonly ChatSettings _chats;
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

    /// <summary>Whether the Raven panel is open or folded to its rail, as stored; the panel owns it (see <see cref="Raven.RavenPanelViewModel.IsOpen"/>).</summary>
    [ObservableProperty] private bool _ravenPanelOpen = true;

    /// <summary>The microphone Raven records from, as stored; the panel owns the choice and falls back when it is gone.</summary>
    [ObservableProperty] private MicrophoneDevice? _ravenMicrophone;

    /// <summary>The model Raven's brain answers with; a change takes effect with the next question, which starts a new conversation.</summary>
    [ObservableProperty] private string _ravenBrainModel = BrainSettings.DefaultModel;

    /// <summary>What a chat Raven starts runs with unless said otherwise: an alias name or model id, or <see cref="ClaudeDefault"/>.</summary>
    [ObservableProperty] private string _ravenChatModel = ClaudeDefault;

    /// <summary>The effort level such a chat runs at, or <see cref="ClaudeDefault"/>.</summary>
    [ObservableProperty] private string _ravenChatEffort = ClaudeDefault;

    /// <summary>The alias table as typed: one <c>Name = model id</c> a line.</summary>
    [ObservableProperty] private string _ravenModelAliases = ChatModels.FormatAliases(ChatModels.DefaultAliases);

    /// <summary>Whether Raven keeps its replies to itself, as stored; the panel owns it (see <see cref="Raven.RavenPanelViewModel.IsMuted"/>).</summary>
    [ObservableProperty] private bool _ravenMuted;

    /// <summary>The preset voice Raven speaks with: an id of <see cref="SpeechSettings.Voices"/>.</summary>
    [ObservableProperty] private string _ravenVoice = SpeechSettings.DefaultVoice;

    /// <summary>The Qwen3-TTS model Raven speaks with; a change restarts the voice.</summary>
    [ObservableProperty] private SpeechModel _ravenVoiceModel = SpeechModel.Small;

    /// <summary>What the model and effort boxes show for "leave it to Claude Code"; stored as blank.</summary>
    public const string ClaudeDefault = "default";

    public SettingsViewModel(ClaudeHookInstaller installer, ISettingsStore settings, PersistenceWriterOptions writerOptions, BrainSettings brain,
        ChatSettings chats, SpeechSettings speech, AppPaths paths, ClaudeCodePaths claude, ILogger<SettingsViewModel> logger)
    {
        _installer = installer;
        _settings = settings;
        _writerOptions = writerOptions;
        _brain = brain;
        _chats = chats;
        _speech = speech;
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
            TileScale = await LoadOrDefaultAsync<double?>(SettingKeys.TileScale, "the tile size", ct) ?? 1;
            RavenPanelOpen = await LoadOrDefaultAsync<bool?>(SettingKeys.RavenPanelOpen, "the Raven panel state", ct) ?? true;
            RavenMicrophone = await LoadOrDefaultAsync<MicrophoneDevice>(SettingKeys.RavenMicrophone, "the Raven microphone", ct);
            RavenBrainModel = await LoadOrDefaultAsync<string>(SettingKeys.RavenBrainModel, "the Raven brain model", ct) is { Length: > 0 } model
                ? model
                : BrainSettings.DefaultModel;
            RavenModelAliases = await LoadOrDefaultAsync<string>(SettingKeys.RavenModelAliases, "the model aliases", ct) is { Length: > 0 } aliases
                ? aliases
                : ChatModels.FormatAliases(ChatModels.DefaultAliases);
            RavenChatModel = OrDefault(await LoadOrDefaultAsync<string>(SettingKeys.RavenChatModel, "the chat model", ct));
            RavenChatEffort = OrDefault(await LoadOrDefaultAsync<string>(SettingKeys.RavenChatEffort, "the chat effort", ct));
            RavenMuted = await LoadOrDefaultAsync<bool?>(SettingKeys.RavenMuted, "whether Raven is muted", ct) ?? false;
            RavenVoice = await LoadOrDefaultAsync<string>(SettingKeys.RavenVoice, "Raven's voice", ct) is { Length: > 0 } voice
                && SpeechSettings.Voices.Any(v => v.Id == voice)
                ? voice
                : SpeechSettings.DefaultVoice;
            RavenVoiceModel = await LoadOrDefaultAsync<SpeechModel?>(SettingKeys.RavenVoiceModel, "Raven's voice model", ct) is { } speechModel
                && Enum.IsDefined(speechModel)
                ? speechModel
                : SpeechModel.Small;
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

    /// <summary>A drag of the slider changes the value many times; the save queue keeps only the latest.</summary>
    partial void OnTileScaleChanged(double value) => Persist(SettingKeys.TileScale, value);

    /// <summary>
    /// In a try of its own: a stored value of the wrong shape (edited by hand) reads as missing, without a message in the
    /// Settings view and without losing the settings read after it.
    /// </summary>
    private async Task<T?> LoadOrDefaultAsync<T>(string key, string what, CancellationToken ct)
    {
        try
        {
            return await _settings.GetAsync<T>(key, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reading {Setting} failed; its default applies", what);
            return default;
        }
    }

    partial void OnRavenPanelOpenChanged(bool value) => Persist(SettingKeys.RavenPanelOpen, value);

    partial void OnRavenMicrophoneChanged(MicrophoneDevice? value) => Persist(SettingKeys.RavenMicrophone, value);

    partial void OnRavenMutedChanged(bool value) => Persist(SettingKeys.RavenMuted, value);

    partial void OnRavenVoiceChanged(string value)
    {
        _speech.Voice = value;
        Persist(SettingKeys.RavenVoice, _speech.Voice);
    }

    partial void OnRavenVoiceModelChanged(SpeechModel value)
    {
        _speech.Model = value;
        Persist(SettingKeys.RavenVoiceModel, value);
    }

    /// <summary>The voices the Settings view offers.</summary>
    public static IReadOnlyList<SpeechVoice> VoiceChoices => SpeechSettings.Voices;

    /// <summary>The voice models the Settings view offers.</summary>
    public static IReadOnlyList<VoiceModelChoice> VoiceModelChoices { get; } =
    [
        new(SpeechModel.Small, "Qwen3-TTS 0.6B (faster)"),
        new(SpeechModel.Large, "Qwen3-TTS 1.7B (sounds better, a little slower)"),
    ];

    /// <summary>Stored as typed, and not tidied in the box while it is typed in; the brain trims it, and runs the default model for a blank one.</summary>
    partial void OnRavenBrainModelChanged(string value)
    {
        _brain.Model = value;
        Persist(SettingKeys.RavenBrainModel, value);
    }

    /// <summary>The models the Settings view offers to pick from; any other id can be typed.</summary>
    public static IReadOnlyList<string> KnownBrainModels => BrainSettings.KnownModels;

    /// <summary>The effort levels the Settings view offers, Claude Code's own default first.</summary>
    public static IReadOnlyList<string> EffortChoices { get; } = [ClaudeDefault, .. ChatModels.EffortLevels];

    /// <summary>The model names the Settings view offers for chats: Claude Code's default, then the aliases.</summary>
    public IReadOnlyList<string> ChatModelChoices => [ClaudeDefault, .. _chats.Aliases.Select(a => a.Name)];

    /// <summary>Raven changed the defaults by voice ("from now on Opus"): shown here, stored, and used from the next chat on. UI thread.</summary>
    public void SetChatDefaults(ChatDefaults defaults)
    {
        RavenChatModel = OrDefault(defaults.Model);
        RavenChatEffort = OrDefault(defaults.Effort);
    }

    partial void OnRavenChatModelChanged(string value)
    {
        _chats.Defaults = _chats.Defaults with { Model = Blank(value) };
        Persist(SettingKeys.RavenChatModel, Blank(value) ?? "");
    }

    partial void OnRavenChatEffortChanged(string value)
    {
        _chats.Defaults = _chats.Defaults with { Effort = Blank(value) };
        Persist(SettingKeys.RavenChatEffort, Blank(value) ?? "");
    }

    /// <summary>Stored as typed; a table with no line of the right shape counts as the default one.</summary>
    partial void OnRavenModelAliasesChanged(string value)
    {
        _chats.Aliases = ChatModels.ParseAliases(value);
        OnPropertyChanged(nameof(ChatModelChoices));
        Persist(SettingKeys.RavenModelAliases, value);
    }

    /// <summary>As <see cref="ChatSettings.Blank"/>, and the choice that stands for Claude Code's default is none too.</summary>
    private static string? Blank(string? value) =>
        ChatSettings.Blank(value) is { } given && !given.Equals(ClaudeDefault, StringComparison.OrdinalIgnoreCase) ? given : null;

    private static string OrDefault(string? value) => Blank(value) ?? ClaudeDefault;

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

/// <summary>A voice model as the Settings view offers it.</summary>
public sealed record VoiceModelChoice(SpeechModel Model, string Label);
