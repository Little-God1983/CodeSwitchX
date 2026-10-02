using System.Diagnostics;
using System.IO;
using CodeSwitchX.Conductor;
using CodeSwitchX.Core;
using Path = System.IO.Path;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Data;
using CodeSwitchX.Ingest.Hooks;
using CodeSwitchX.UI.Voice;
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Dictation;
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
    private readonly SpeechEngines _engines;
    private readonly IWhisperModelStore _whisper;
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

    /// <summary>Whether Raven tells what the chats did; off, its digest cards are only written. On by default.</summary>
    [ObservableProperty] private bool _ravenSpeakNews = true;

    /// <summary>The mic mode as stored ("PushToTalk", "OpenMic"); the panel owns it (see <see cref="Raven.RavenPanelViewModel.MicMode"/>).</summary>
    [ObservableProperty] private string _ravenMicMode = nameof(Raven.MicMode.PushToTalk);

    [ObservableProperty] private bool _ravenBargeIn = true;

    /// <summary>The engine Raven speaks with: a name of <see cref="SpeechEngine"/>, or <see cref="NoEngine"/> (Raven only writes).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RavenVoice), nameof(VoiceChoices), nameof(HasEngine), nameof(IsQwen))]
    private string _ravenVoiceEngine = NoEngine;

    /// <summary>Qwen3-TTS's preset voice: an id of <see cref="SpeechSettings.QwenVoices"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RavenVoice))]
    private string _ravenQwenVoice = SpeechSettings.DefaultQwenVoice;

    /// <summary>Kokoro's voice: an id of <see cref="SpeechSettings.KokoroVoices"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RavenVoice))]
    private string _ravenKokoroVoice = SpeechSettings.DefaultKokoroVoice;

    /// <summary>The Qwen3-TTS model Raven speaks with; a change restarts the voice.</summary>
    [ObservableProperty] private SpeechModel _ravenVoiceModel = SpeechModel.Small;

    /// <summary>Whether the voice setup opened once by itself: then it opens only from here.</summary>
    [ObservableProperty] private bool _ravenVoiceSetupShown;

    /// <summary>The Whisper model dictation uses; a change frees the one loaded and loads this one.</summary>
    [ObservableProperty] private WhisperModel _ravenWhisperModel = WhisperModel.LargeV3Turbo;

    /// <summary>The speech-to-text model picked is not on disk: the Download button shows.</summary>
    [ObservableProperty] private bool _whisperMissing;

    /// <summary>What <see cref="RavenVoiceEngine"/> holds when no engine is picked.</summary>
    public const string NoEngine = "None";

    /// <summary>What the model and effort boxes show for "leave it to Claude Code"; stored as blank.</summary>
    public const string ClaudeDefault = "default";

    public SettingsViewModel(ClaudeHookInstaller installer, ISettingsStore settings, PersistenceWriterOptions writerOptions, BrainSettings brain,
        ChatSettings chats, SpeechSettings speech, SpeechEngines engines, IWhisperModelStore whisper, VoiceStatusViewModel voice, AppPaths paths,
        ClaudeCodePaths claude, ILogger<SettingsViewModel> logger)
    {
        _installer = installer;
        _settings = settings;
        _writerOptions = writerOptions;
        _brain = brain;
        _chats = chats;
        _speech = speech;
        _engines = engines;
        _whisper = whisper;
        Voice = voice;
        Voice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VoiceStatusViewModel.ListeningState))
            {
                WhisperMissing = Voice.ListeningState == DictationState.NotDownloaded;
            }
        };
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
            RavenSpeakNews = await LoadOrDefaultAsync<bool?>(SettingKeys.RavenSpeakNews, "whether Raven speaks chat news", ct) ?? true;
            RavenMicMode = await LoadOrDefaultAsync<string?>(SettingKeys.RavenMicMode, "Raven's mic mode", ct) ?? nameof(Raven.MicMode.PushToTalk);
            RavenBargeIn = await LoadOrDefaultAsync<bool?>(SettingKeys.RavenBargeIn, "whether talking over Raven stops it", ct) ?? true;
            _speech.QwenVoice = await LoadOrDefaultAsync<string>(SettingKeys.RavenVoice, "Raven's voice", ct) ?? SpeechSettings.DefaultQwenVoice;
            RavenQwenVoice = _speech.QwenVoice; // the setter keeps a known voice, or the default
            _speech.KokoroVoice = await LoadOrDefaultAsync<string>(SettingKeys.RavenKokoroVoice, "Raven's Kokoro voice", ct) ?? SpeechSettings.DefaultKokoroVoice;
            RavenKokoroVoice = _speech.KokoroVoice;
            RavenVoiceModel = await LoadOrDefaultAsync<SpeechModel?>(SettingKeys.RavenVoiceModel, "Raven's voice model", ct) is { } speechModel
                && Enum.IsDefined(speechModel)
                ? speechModel
                : SpeechModel.Small;
            RavenVoiceSetupShown = await LoadOrDefaultAsync<bool?>(SettingKeys.RavenVoiceSetupShown, "whether the voice setup was shown", ct) ?? false;
            // Never picked: Qwen3-TTS for whoever has it installed already (it was the only engine), else none until the
            // voice setup. Not stored, so it is looked at again until a pick is.
            RavenVoiceEngine = await LoadOrDefaultAsync<string>(SettingKeys.RavenVoiceEngine, "Raven's voice engine", ct) is { Length: > 0 } engine
                ? (Enum.TryParse<SpeechEngine>(engine, out var known) ? known.ToString() : NoEngine)
                : await Task.Run(() => _engines.IsInstalled(SpeechEngine.Qwen), ct) ? nameof(SpeechEngine.Qwen) : NoEngine;
            RavenWhisperModel = await LoadOrDefaultAsync<WhisperModel?>(SettingKeys.RavenWhisperModel, "the speech-to-text model", ct) is { } whisperModel
                && Enum.IsDefined(whisperModel)
                ? whisperModel
                : WhisperModel.LargeV3Turbo;
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
            HookInstallState.Outdated => "Installed, but the entries are out of date (an older path or form, or none yet for chats' questions). Install again to update them.",
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

    partial void OnRavenSpeakNewsChanged(bool value) => Persist(SettingKeys.RavenSpeakNews, value);

    partial void OnRavenMicModeChanged(string value) => Persist(SettingKeys.RavenMicMode, value);

    partial void OnRavenBargeInChanged(bool value) => Persist(SettingKeys.RavenBargeIn, value);

    partial void OnRavenQwenVoiceChanged(string value)
    {
        _speech.QwenVoice = value;
        Persist(SettingKeys.RavenVoice, _speech.QwenVoice);
    }

    partial void OnRavenKokoroVoiceChanged(string value)
    {
        _speech.KokoroVoice = value;
        Persist(SettingKeys.RavenKokoroVoice, _speech.KokoroVoice);
    }

    partial void OnRavenVoiceModelChanged(SpeechModel value)
    {
        _speech.Model = value;
        Persist(SettingKeys.RavenVoiceModel, value);
    }

    partial void OnRavenVoiceEngineChanged(string value)
    {
        _speech.Engine = Engine;
        Persist(SettingKeys.RavenVoiceEngine, value);
    }

    partial void OnRavenVoiceSetupShownChanged(bool value) => Persist(SettingKeys.RavenVoiceSetupShown, value);

    partial void OnRavenWhisperModelChanged(WhisperModel value)
    {
        _whisper.Model = value;
        Persist(SettingKeys.RavenWhisperModel, value);
    }

    /// <summary>Where Raven's models stand, for the dots beside the engine and the speech-to-text model.</summary>
    public VoiceStatusViewModel Voice { get; }

    /// <summary>The engine picked, or none.</summary>
    public SpeechEngine? Engine => Enum.TryParse<SpeechEngine>(RavenVoiceEngine, out var engine) ? engine : null;

    public bool HasEngine => Engine is not null;

    /// <summary>The model choice is Qwen3-TTS's only.</summary>
    public bool IsQwen => Engine == SpeechEngine.Qwen;

    /// <summary>The voice of the engine picked, as the Voice box shows and changes it.</summary>
    public string RavenVoice
    {
        get => Engine == SpeechEngine.Kokoro ? RavenKokoroVoice : RavenQwenVoice;
        set
        {
            if (value is null)
            {
                return; // the box clears its selection as its list changes with the engine
            }

            if (Engine == SpeechEngine.Kokoro)
            {
                RavenKokoroVoice = value;
            }
            else
            {
                RavenQwenVoice = value;
            }
        }
    }

    /// <summary>The voices of the engine picked.</summary>
    public IReadOnlyList<SpeechVoice> VoiceChoices => SpeechSettings.VoicesOf(Engine ?? SpeechEngine.Kokoro);

    /// <summary>The engines the Settings view offers, none first.</summary>
    public static IReadOnlyList<EngineChoice> EngineChoices { get; } =
    [
        new(NoEngine, "None: Raven answers in text"),
        new(nameof(SpeechEngine.Kokoro), "Kokoro (small, runs on any PC)"),
        new(nameof(SpeechEngine.Qwen), "Qwen3-TTS (more natural, needs an NVIDIA graphics card)"),
    ];

    /// <summary>The speech-to-text models the Settings view offers.</summary>
    public static IReadOnlyList<WhisperChoice> WhisperChoices { get; } =
    [
        new(WhisperModel.TinyEnglish, "Tiny: English only, 78 MB, the fastest"),
        new(WhisperModel.BaseEnglish, "Base: English only, 148 MB"),
        new(WhisperModel.SmallEnglish, "Small: English only, 488 MB"),
        new(WhisperModel.LargeV3Turbo, "Large v3 Turbo: any language, 1.6 GB, best on a graphics card"),
    ];

    /// <summary>The voice setup picked an engine and a voice: shown here and stored, and Raven speaks with them from now on.</summary>
    public void PickVoice(SpeechEngine engine, string voice)
    {
        if (engine == SpeechEngine.Kokoro)
        {
            RavenKokoroVoice = voice;
        }
        else
        {
            RavenQwenVoice = voice;
        }

        RavenVoiceEngine = engine.ToString();
    }

    /// <summary>The voice setup opens by itself once: when the Raven panel is first used and no engine is picked.</summary>
    public bool NeedsVoiceSetup => !RavenVoiceSetupShown && Engine is null;

    /// <summary>Asks for the voice setup; the shell opens it.</summary>
    public event Action? VoiceSetupRequested;

    [RelayCommand]
    private void OpenVoiceSetup() => VoiceSetupRequested?.Invoke();

    /// <summary>Downloads the speech-to-text model picked; the dot beside it shows how far it is.</summary>
    [RelayCommand]
    private async Task DownloadWhisperAsync()
    {
        try
        {
            await _whisper.DownloadAsync(null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The speech model could not be downloaded");
            LastMessage = $"The speech-to-text model could not be downloaded: {ex.Message}";
        }
    }

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

/// <summary>An engine as the Settings view offers it: a name of <see cref="SpeechEngine"/>, or <see cref="SettingsViewModel.NoEngine"/>.</summary>
public sealed record EngineChoice(string Engine, string Label);

/// <summary>A speech-to-text model as the Settings view offers it.</summary>
public sealed record WhisperChoice(WhisperModel Model, string Label);
