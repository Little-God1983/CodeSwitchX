using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CodeSwitchX.Conductor;
using CodeSwitchX.Core;
using Path = System.IO.Path;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Data;
using CodeSwitchX.Ingest.Hooks;
using CodeSwitchX.UI.Infrastructure;
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
    private readonly IUiDispatcher _ui;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IChatSounds? _sounds;
    private readonly SoundPicks _picks;
    private readonly SoundLibrary _library;
    private readonly ISoundFilePicker? _soundFiles;
    private readonly AudioLevels _levels;
    private bool _loading;
    private readonly Lock _saveGate = new();
    private readonly Dictionary<string, Func<CancellationToken, Task>> _pendingSaves = [];
    private Task _drain = Task.CompletedTask;
    private bool _draining;

    [ObservableProperty] private string _relayExecutable;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HooksInPlace), nameof(HookHeading))]
    private HookInstallState _hookState;
    [ObservableProperty] private string _hookStatusText = string.Empty;
    [ObservableProperty] private string? _lastMessage;
    [ObservableProperty] private bool _storePayloads;
    [ObservableProperty] private long? _fiveHourBudgetTokens;

    /// <summary>The Yard's tile size as stored; the Yard owns it (see <see cref="Yard.YardViewModel.TileScale"/>).</summary>
    [ObservableProperty] private double _tileScale = 1;

    /// <summary>The Yard's font size as stored; the Yard owns it (see <see cref="Yard.YardViewModel.FontScale"/>).</summary>
    [ObservableProperty] private double _fontScale = 1;

    /// <summary>Whether the Raven panel is open or folded to its rail, as stored; the panel owns it (see <see cref="Raven.RavenPanelViewModel.IsOpen"/>).</summary>
    [ObservableProperty] private bool _ravenPanelOpen = true;

    /// <summary>The microphone Raven records from, as stored; the panel owns the choice and falls back when it is gone.</summary>
    [ObservableProperty] private MicrophoneDevice? _ravenMicrophone;

    /// <summary>The output Raven speaks on, as stored: the default picked in Settings; null follows the Windows default.</summary>
    [ObservableProperty] private SpeakerDevice? _ravenSpeaker;

    /// <summary>The model Raven's brain answers with; a change takes effect with the next question, which starts a new conversation.</summary>
    [ObservableProperty] private string _ravenBrainModel = BrainSettings.DefaultModel;

    /// <summary>The model chat 0, the overview, answers with, and its chat summaries are worded with (#124).</summary>
    [ObservableProperty] private string _ravenOverviewModel = BrainSettings.DefaultOverviewModel;

    /// <summary>
    /// The effort all of Raven's brains think at, or <see cref="ClaudeDefault"/> (#201); a change takes effect with the
    /// next question, which carries the conversation on.
    /// </summary>
    [ObservableProperty] private string _ravenEffort = ClaudeDefault;

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

    /// <summary>How long, in seconds, after Raven spoke or answered an Open mic turn needs no "Raven" before it (#217): one of
    /// <see cref="FollowUpChoices"/>, 0 for none.</summary>
    [ObservableProperty] private int _ravenFollowUpSeconds = Raven.RavenPanelViewModel.DefaultFollowUpSeconds;

    /// <summary>How long, in seconds, whatever Raven says on its own waits after it last spoke or made a sound (#152): one of <see cref="PauseChoices"/>.</summary>
    [ObservableProperty] private int _ravenPauseSeconds = (int)Raven.TrafficWatcher.DefaultPause.TotalSeconds;

    /// <summary>The windows whose Raven chat is muted (#153), by workspace id; the panel owns it, Settings stores it.</summary>
    [ObservableProperty] private IReadOnlyList<Guid> _ravenMutedWindows = [];

    /// <summary>Whether Raven says what came in a chat while the user was away when they switch to it (#127). On by default (#143).</summary>
    [ObservableProperty] private bool _ravenCatchUp = true;

    /// <summary>How loud the chats' sounds play, 0..100 % (#305); the Sounds page's slider.</summary>
    [ObservableProperty] private int _ravenSoundVolume = DefaultSoundVolume;

    /// <summary>How loud Raven's voice plays, 0..100 % (#305): answers, news, the samples. Full is as before there was a slider.</summary>
    [ObservableProperty] private int _ravenVoiceVolume = DefaultVoiceVolume;

    public const int DefaultSoundVolume = AudioLevels.DefaultSoundsPercent, DefaultVoiceVolume = AudioLevels.DefaultVoicePercent;

    /// <summary>A slot with no sound (#306), as stored and as its picker holds it: a picker cannot select an item whose value is null.</summary>
    public const string NoSound = "none";

    /// <summary>A sound a slot's picker offers: a sound of the pool, or None.</summary>
    public sealed record SoundChoice(string Id, string Name);

    /// <summary>
    /// What each slot's picker offers: None, then the pool (#307: the user's sounds after the shipped ones). Kept in step item by
    /// item, never rebuilt: a picker whose item went away would write an empty pick back into its slot.
    /// </summary>
    public ObservableCollection<SoundChoice> SoundChoices { get; } = [];

    /// <summary>A sound of the user's as the Sounds page lists it: its name and how long it plays ("1.2 s").</summary>
    public sealed record UserSoundRow(string Id, string Name, string Length);

    /// <summary>The sounds the user added (#307), in the order they were added.</summary>
    public ObservableCollection<UserSoundRow> UserSounds { get; } = [];

    /// <summary>What the last sound added left to say: the 3 s notice, or why the file was refused; null for nothing.</summary>
    [ObservableProperty] private string? _soundNotice;

    /// <summary>The sound a chat makes when it starts working (#306): a shipped sound's id, or <see cref="NoSound"/>.</summary>
    [ObservableProperty] private string _ravenSoundStart = ShippedSounds.DefaultFor(SoundSlot.StartsWorking);

    /// <summary>The sound a chat makes when its turn ends (#306).</summary>
    [ObservableProperty] private string _ravenSoundFinish = ShippedSounds.DefaultFor(SoundSlot.Finishes);

    /// <summary>The sound a chat makes when it asks a question, or waits for the user without a card (#306).</summary>
    [ObservableProperty] private string _ravenSoundQuestion = ShippedSounds.DefaultFor(SoundSlot.AsksQuestion);

    /// <summary>The sound a chat makes when it asks permission (#306).</summary>
    [ObservableProperty] private string _ravenSoundPermission = ShippedSounds.DefaultFor(SoundSlot.AsksPermission);

    /// <summary>The sound a chat makes when its turn fails (#306).</summary>
    [ObservableProperty] private string _ravenSoundFail = ShippedSounds.DefaultFor(SoundSlot.Fails);

    /// <summary>The sound Raven makes when Open mic hears its name (#306): at once, past the queue.</summary>
    [ObservableProperty] private string _ravenSoundName = ShippedSounds.DefaultFor(SoundSlot.HearsName);

    /// <summary>How long, in minutes, a chat stays on its tile after its tab was closed (#164): one of <see cref="KeepClosedChoices"/>, 0 for not at all.</summary>
    [ObservableProperty] private int _yardKeepClosedMinutes;

    /// <summary>How long, in hours, a chat may be idle before its tile hides it (#164): one of <see cref="HideIdleChoices"/>, 0 for never.</summary>
    [ObservableProperty] private int _yardHideIdleHours;

    /// <summary>The most chat rows a tile shows (#295): one of <see cref="MaxChatsChoices"/>; the rest are a "+N more" line.</summary>
    [ObservableProperty] private int _yardMaxChats = Yard.YardViewModel.DefaultMaxChats;

    /// <summary>The most chats a tile can show, as the Yard page offers them.</summary>
    public IReadOnlyList<SettingChoice> MaxChatsChoices { get; } =
        [.. Yard.YardViewModel.MaxChatsChoices.Select(n => new SettingChoice(n, MaxChatsText(n)))];

    /// <summary>The times a closed chat can be kept for, as the Yard page offers them.</summary>
    public IReadOnlyList<SettingChoice> KeepClosedChoices { get; } =
        [.. Yard.YardViewModel.KeepClosedMinutesChoices.Select(m => new SettingChoice(m, KeepClosedText(m)))];

    /// <summary>The idle times after which a chat is hidden, as the Yard page offers them.</summary>
    public IReadOnlyList<SettingChoice> HideIdleChoices { get; } =
        [.. Yard.YardViewModel.HideIdleHoursChoices.Select(h => new SettingChoice(h, HideIdleText(h)))];

    public static string KeepClosedText(int minutes) => minutes == 0 ? "Off" : minutes == 1 ? "1 minute" : $"{minutes} minutes";

    public static string MaxChatsText(int chats) => $"{chats} chats";

    public static string HideIdleText(int hours) => hours == 0 ? "Never" : hours == 1 ? "1 hour" : $"{hours} hours";

    /// <summary>
    /// The sidebar shows only the pages' icons, so a narrow window keeps room for the page (#162); the shell sets it from
    /// the window's width.
    /// </summary>
    [ObservableProperty] private bool _isSidebarFolded;

    /// <summary>The search put aside while the sidebar is folded, given back as it unfolds.</summary>
    private string _searchBeforeFold = "";

    /// <summary>
    /// Folded, the search box is gone: a search left in it would filter the pages with no way to see or clear it. It is put
    /// aside, and back as the sidebar unfolds, so a window that is narrow only for a moment does not lose it; but only
    /// while it still finds the page shown, so it never takes the user off a page they picked meanwhile.
    /// </summary>
    partial void OnIsSidebarFoldedChanged(bool value)
    {
        if (value)
        {
            _searchBeforeFold = Search;
            Search = "";
        }
        else
        {
            if (SelectedPage.Matches(_searchBeforeFold))
            {
                Search = _searchBeforeFold;
            }

            _searchBeforeFold = "";
        }
    }

    /// <summary>The pauses between messages the Voice page offers, in seconds.</summary>
    public IReadOnlyList<int> PauseChoices => Raven.TrafficWatcher.PauseChoices;

    public IReadOnlyList<int> FollowUpChoices => Raven.RavenPanelViewModel.FollowUpChoices;

    /// <summary>The engine Raven speaks with: a name of <see cref="SpeechEngine"/>, or <see cref="NoEngine"/> (Raven only writes).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQwen), nameof(IsKokoro))]
    private string _ravenVoiceEngine = NoEngine;

    /// <summary>Qwen3-TTS's preset voice: an id of <see cref="SpeechSettings.QwenVoices"/>.</summary>
    [ObservableProperty] private string _ravenQwenVoice = SpeechSettings.DefaultQwenVoice;

    /// <summary>Kokoro's voice: an id of <see cref="SpeechSettings.KokoroVoices"/>.</summary>
    [ObservableProperty] private string _ravenKokoroVoice = SpeechSettings.DefaultKokoroVoice;

    /// <summary>The Qwen3-TTS model Raven speaks with; a change restarts the voice.</summary>
    [ObservableProperty] private SpeechModel _ravenVoiceModel = SpeechModel.Small;

    /// <summary>Whether Settings → Voice opened once by itself on the first run: then only the user opens it. The key keeps its older name.</summary>
    [ObservableProperty] private bool _ravenVoiceSetupShown;

    /// <summary>The Whisper model dictation uses; a change frees the one loaded and loads this one.</summary>
    [ObservableProperty] private WhisperModel _ravenWhisperModel = WhisperModel.LargeV3Turbo;

    /// <summary>The page shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Page), nameof(ListedPage))]
    private SettingsPageItem _selectedPage = SettingsPageItem.All[0];

    /// <summary>What the sidebar's search box holds: it lists the pages with a setting of that name.</summary>
    [ObservableProperty] private string _search = "";

    /// <summary>What <see cref="RavenVoiceEngine"/> holds when no engine is picked.</summary>
    public const string NoEngine = "None";

    /// <summary>What the model and effort boxes show for "leave it to Claude Code"; stored as blank.</summary>
    public const string ClaudeDefault = "default";

    public SettingsViewModel(ClaudeHookInstaller installer, ISettingsStore settings, PersistenceWriterOptions writerOptions, BrainSettings brain,
        ChatSettings chats, SpeechSettings speech, SpeechEngines engines, IWhisperModelStore whisper, VoiceStatusViewModel voice, AppPaths paths,
        ClaudeCodePaths claude, IVoiceSamples samples, IUiDispatcher ui, ILogger<SettingsViewModel> logger, ChatHotkeys? shortcuts = null,
        IChatSounds? sounds = null, AudioLevels? levels = null, SoundPicks? picks = null, SoundLibrary? library = null,
        ISoundFilePicker? soundFiles = null)
    {
        _sounds = sounds;
        _library = library ?? new SoundLibrary(null);
        _picks = picks ?? new SoundPicks(_library);
        _soundFiles = soundFiles;
        ShowSounds();
        _library.Changed += (_, _) => ShowSounds();
        _levels = levels ?? new AudioLevels();
        ShowLevels();
        Shortcuts = shortcuts ?? new ChatHotkeys(settings);
        _installer = installer;
        _settings = settings;
        _writerOptions = writerOptions;
        _brain = brain;
        _chats = chats;
        _speech = speech;
        _engines = engines;
        _whisper = whisper;
        _ui = ui;
        Voice = voice;
        _logger = logger;
        DataFolder = paths.Root;
        LogsFolder = paths.LogsDirectory;
        SettingsFile = claude.SettingsFile;
        _relayExecutable = DefaultRelayExecutable;
        Pages = [.. SettingsPageItem.All];
        WhisperRows =
        [
            new(WhisperModel.TinyEnglish, "English only, the fastest", "78 MB", DownloadWhisperAsync),
            new(WhisperModel.BaseEnglish, "English only", "148 MB", DownloadWhisperAsync),
            new(WhisperModel.SmallEnglish, "English only, more accurate", "488 MB", DownloadWhisperAsync),
            new(WhisperModel.LargeV3Turbo, "Any language, the most accurate", "1.6 GB", DownloadWhisperAsync),
        ];
        Aliases = [];
        ShowAliases();
        VoicePage = new VoicePageViewModel(this, engines, voice, samples, logger);
        Voice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(VoiceStatusViewModel.Listening) or nameof(VoiceStatusViewModel.ListeningState))
            {
                ShowWhisperRows();
            }
        };
        _whisper.DownloadChanged += (_, _) => _ui.Post(ShowWhisperRows);
        ShowWhisperRows();
    }

    /// <summary>The Voice page: engine cards, voices and samples.</summary>
    public VoicePageViewModel VoicePage { get; }

    /// <summary>The pages the sidebar lists: those the search finds.</summary>
    public ObservableCollection<SettingsPageItem> Pages { get; }

    /// <summary>The page shown, for the view to pick its content by.</summary>
    public SettingsPage Page => SelectedPage.Page;

    /// <summary>
    /// The sidebar's selection. A page the search hides leaves the list with none, and the list clears its selection:
    /// the page shown stays (<see cref="SelectedPage"/>).
    /// </summary>
    public SettingsPageItem? ListedPage
    {
        get => Pages.Contains(SelectedPage) ? SelectedPage : null;
        set
        {
            if (value is not null)
            {
                SelectedPage = value;
            }
        }
    }

    /// <summary>Shows <paramref name="page"/>, with every page listed again.</summary>
    public void OpenPage(SettingsPage page)
    {
        Search = "";
        _searchBeforeFold = "";
        SelectedPage = SettingsPageItem.All.First(p => p.Page == page);
    }

    /// <summary>Settings closed: a sample still playing stops, and the welcome line has had its turn.</summary>
    public void Closed()
    {
        VoicePage.StopSample();
        VoicePage.ShowWelcome = false;
    }

    partial void OnSearchChanged(string value)
    {
        var found = SettingsPageItem.All.Where(p => p.Matches(value)).ToList();
        Pages.Clear();
        foreach (var page in found)
        {
            Pages.Add(page);
        }

        // The page shown stays while the search finds it, or finds nothing; else the first page found shows.
        if (found.Count > 0 && !found.Contains(SelectedPage))
        {
            SelectedPage = found[0];
        }

        OnPropertyChanged(nameof(ListedPage));
    }

    /// <summary>The speech-to-text models, each with its size, state and Download.</summary>
    public IReadOnlyList<SpeechToTextRow> WhisperRows { get; }

    private void ShowWhisperRows()
    {
        var downloads = _whisper.Downloads;
        foreach (var row in WhisperRows)
        {
            row.Show(downloads.FirstOrDefault(d => d.Model == row.Model), _whisper.IsPresentOf(row.Model),
                row.Model == RavenWhisperModel && Voice.ListeningState is not (null or DictationState.NotDownloaded or DictationState.Downloading)
                    ? Voice.Listening
                    : null);
        }
    }

    /// <summary>The model names table, a row a name; rows not filled in yet stay here and are left out of what is stored.</summary>
    public ObservableCollection<AliasRow> Aliases { get; }

    /// <summary>Set while the table writes <see cref="RavenModelAliases"/>: the table is not built again from it.</summary>
    private bool _writingAliases;

    /// <summary>No row of the table counts: the default names apply, and a line under the table says so.</summary>
    [ObservableProperty] private bool _aliasesFallBack;

    /// <summary>The line under a table with no name that counts.</summary>
    public static string AliasesFallBackText { get; } =
        $"No name in the table counts, so the default names apply: {string.Join(", ", ChatModels.DefaultAliases.Select(a => a.Name))}.";

    private void ShowAliases()
    {
        Aliases.Clear();
        foreach (var alias in ChatModels.ParseAliases(RavenModelAliases))
        {
            Aliases.Add(NewAliasRow(alias.Name, alias.Id));
        }

        ShowAliasProblems();
    }

    /// <summary>
    /// Each row the parser leaves out says why, and a table with none that counts says the default names apply: the
    /// table shows what Raven goes by, not only what was typed.
    /// </summary>
    private IReadOnlyList<ModelAlias> ShowAliasProblems()
    {
        var problems = ChatModels.RowProblems([.. Aliases.Select(a => new ModelAlias(a.Name, a.Id))]);
        var counted = new List<ModelAlias>();
        for (var i = 0; i < Aliases.Count; i++)
        {
            Aliases[i].Problem = problems[i];
            if (problems[i] is null && !string.IsNullOrWhiteSpace(Aliases[i].Name))
            {
                counted.Add(new ModelAlias(Aliases[i].Name.Trim(), Aliases[i].Id.Trim()));
            }
        }

        AliasesFallBack = counted.Count == 0;
        return counted;
    }

    private AliasRow NewAliasRow(string name, string id) => new(name, id, _ => WriteAliases(), row =>
    {
        Aliases.Remove(row);
        WriteAliases();
    });

    private void WriteAliases()
    {
        _writingAliases = true;
        try
        {
            RavenModelAliases = ChatModels.FormatAliases(ShowAliasProblems());
        }
        finally
        {
            _writingAliases = false;
        }
    }

    /// <summary>A blank row at the end of the table, to type a name and its model id into.</summary>
    [RelayCommand]
    private void AddAlias() => Aliases.Add(NewAliasRow("", ""));

    /// <summary>Installed, partly or with old entries: the card offers Reinstall and Remove; else Install.</summary>
    public bool HooksInPlace => HookState is HookInstallState.Installed or HookInstallState.Partial or HookInstallState.Outdated;

    public string HookHeading => HookState switch
    {
        HookInstallState.Installed => "Hooks installed",
        HookInstallState.Partial or HookInstallState.Outdated => "Hooks need an update",
        HookInstallState.Unreadable => "Hooks unknown",
        _ => "Hooks not installed",
    };

    public static string DefaultRelayExecutable => Path.Combine(AppContext.BaseDirectory, "relay", "csx-hook.exe");

    /// <summary>The hotkeys that switch Raven's chat, on the Shortcuts page.</summary>
    public ChatHotkeys Shortcuts { get; }

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
            // Before #295 the tile size zoomed the text too: until a font size is stored, the text keeps the size it had.
            FontScale = await LoadOrDefaultAsync<double?>(SettingKeys.FontScale, "the tiles' font size", ct) ?? TileScale;
            RavenPanelOpen = await LoadOrDefaultAsync<bool?>(SettingKeys.RavenPanelOpen, "the Raven panel state", ct) ?? true;
            RavenMicrophone = await LoadOrDefaultAsync<MicrophoneDevice>(SettingKeys.RavenMicrophone, "the Raven microphone", ct);
            RavenSpeaker = await LoadOrDefaultAsync<SpeakerDevice>(SettingKeys.RavenSpeaker, "the Raven output", ct);
            RavenBrainModel = await LoadOrDefaultAsync<string>(SettingKeys.RavenBrainModel, "the Raven brain model", ct) is { Length: > 0 } model
                ? model
                : BrainSettings.DefaultModel;
            RavenOverviewModel = await LoadOrDefaultAsync<string>(SettingKeys.RavenOverviewModel, "chat 0's model", ct) is { Length: > 0 } overview
                ? overview
                : BrainSettings.DefaultOverviewModel;
            RavenEffort = OrDefault(ChatModels.ResolveEffort(await LoadOrDefaultAsync<string>(SettingKeys.RavenEffort, "Raven's effort", ct)));
            RavenModelAliases = await LoadOrDefaultAsync<string>(SettingKeys.RavenModelAliases, "the model aliases", ct) is { Length: > 0 } aliases
                ? aliases
                : ChatModels.FormatAliases(ChatModels.DefaultAliases);
            RavenChatModel = OrDefault(await LoadOrDefaultAsync<string>(SettingKeys.RavenChatModel, "the chat model", ct));
            RavenChatEffort = OrDefault(await LoadOrDefaultAsync<string>(SettingKeys.RavenChatEffort, "the chat effort", ct));
            RavenMuted = await LoadOrDefaultAsync<bool?>(SettingKeys.RavenMuted, "whether Raven is muted", ct) ?? false;
            RavenSpeakNews = await LoadOrDefaultAsync<bool?>(SettingKeys.RavenSpeakNews, "whether Raven speaks chat news", ct) ?? true;
            RavenMicMode = await LoadOrDefaultAsync<string?>(SettingKeys.RavenMicMode, "Raven's mic mode", ct) ?? nameof(Raven.MicMode.PushToTalk);
            RavenBargeIn = await LoadOrDefaultAsync<bool?>(SettingKeys.RavenBargeIn, "whether talking over Raven stops it", ct) ?? true;
            RavenFollowUpSeconds = await LoadOrDefaultAsync<int?>(SettingKeys.RavenFollowUpSeconds, "Open mic's follow-up", ct) is { } followUp
                && FollowUpChoices.Contains(followUp) ? followUp : Raven.RavenPanelViewModel.DefaultFollowUpSeconds;
            RavenMutedWindows = await LoadOrDefaultAsync<List<Guid>>(SettingKeys.RavenMutedWindows, "the muted Raven chats", ct) ?? [];
            RavenPauseSeconds = await LoadOrDefaultAsync<int?>(SettingKeys.RavenPauseSeconds, "the pause between messages", ct) is { } pause
                && PauseChoices.Contains(pause) ? pause : (int)Raven.TrafficWatcher.DefaultPause.TotalSeconds;
            YardKeepClosedMinutes = await LoadOrDefaultAsync<int?>(SettingKeys.YardKeepClosedMinutes, "how long a closed chat is kept", ct) is { } keep
                && Yard.YardViewModel.KeepClosedMinutesChoices.Contains(keep) ? keep : 0;
            YardHideIdleHours = await LoadOrDefaultAsync<int?>(SettingKeys.YardHideIdleHours, "when an idle chat is hidden", ct) is { } hide
                && Yard.YardViewModel.HideIdleHoursChoices.Contains(hide) ? hide : 0;
            YardMaxChats = await LoadOrDefaultAsync<int?>(SettingKeys.YardMaxChats, "the most chats on a tile", ct) is { } most
                && Yard.YardViewModel.MaxChatsChoices.Contains(most) ? most : Yard.YardViewModel.DefaultMaxChats;
            RavenCatchUp = await LoadOrDefaultAsync<bool?>(SettingKeys.RavenCatchUp, "whether Raven catches up on switching chats", ct) ?? true;
            RavenSoundVolume = await LoadOrDefaultAsync<int?>(SettingKeys.RavenSoundVolume, "the sounds' volume", ct) is { } sounds and >= 0 and <= 100
                ? sounds : DefaultSoundVolume;
            RavenVoiceVolume = await LoadOrDefaultAsync<int?>(SettingKeys.RavenVoiceVolume, "the voice's volume", ct) is { } voiceVolume and >= 0 and <= 100
                ? voiceVolume : DefaultVoiceVolume;
            RavenSoundStart = await LoadSoundAsync(SettingKeys.RavenSoundStart, SoundSlot.StartsWorking, ct);
            RavenSoundFinish = await LoadSoundAsync(SettingKeys.RavenSoundFinish, SoundSlot.Finishes, ct);
            RavenSoundQuestion = await LoadSoundAsync(SettingKeys.RavenSoundQuestion, SoundSlot.AsksQuestion, ct);
            RavenSoundPermission = await LoadSoundAsync(SettingKeys.RavenSoundPermission, SoundSlot.AsksPermission, ct);
            RavenSoundFail = await LoadSoundAsync(SettingKeys.RavenSoundFail, SoundSlot.Fails, ct);
            RavenSoundName = await LoadSoundAsync(SettingKeys.RavenSoundName, SoundSlot.HearsName, ct);
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
            // first run picks. Not stored, so it is looked at again until a pick is.
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

        // Outside the reads above: one of them failing must not leave the defaults in place of the stored chords, which
        // the next change would then save over. It never throws.
        await Shortcuts.LoadAsync(ct);
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

    partial void OnFontScaleChanged(double value) => Persist(SettingKeys.FontScale, value);

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

    partial void OnRavenSpeakerChanged(SpeakerDevice? value) => Persist(SettingKeys.RavenSpeaker, value);

    partial void OnRavenMutedChanged(bool value) => Persist(SettingKeys.RavenMuted, value);

    partial void OnRavenSpeakNewsChanged(bool value) => Persist(SettingKeys.RavenSpeakNews, value);

    partial void OnRavenMicModeChanged(string value) => Persist(SettingKeys.RavenMicMode, value);

    partial void OnRavenBargeInChanged(bool value) => Persist(SettingKeys.RavenBargeIn, value);

    partial void OnRavenFollowUpSecondsChanged(int value) => Persist(SettingKeys.RavenFollowUpSeconds, value);

    partial void OnRavenPauseSecondsChanged(int value) => Persist(SettingKeys.RavenPauseSeconds, value);

    partial void OnRavenSoundVolumeChanged(int value)
    {
        ShowLevels();
        Persist(SettingKeys.RavenSoundVolume, value);
    }

    partial void OnRavenVoiceVolumeChanged(int value)
    {
        ShowLevels();
        Persist(SettingKeys.RavenVoiceVolume, value);
    }

    /// <summary>The audio plays at the sliders' levels, from the first moment: what plays meanwhile is heard at once.</summary>
    private void ShowLevels()
    {
        _levels.Sounds = AudioLevels.GainOf(RavenSoundVolume);
        _levels.Voice = AudioLevels.GainOf(RavenVoiceVolume);
    }

    /// <summary>Letting go of the sounds' slider plays the finished sound, or the old chime with none, at the level just set.</summary>
    [RelayCommand]
    private void PlaySoundLevel() => Play(_picks.For(SoundSlot.Finishes) ?? "two-tones");

    /// <summary>A slot's play button: its sound as picked, nothing for None.</summary>
    [RelayCommand]
    private void PlaySlotSound(SoundSlot slot)
    {
        if (_picks.For(slot) is { } sound)
        {
            Play(sound);
        }
    }

    private CancellationTokenSource? _playing;

    /// <summary>Plays a sound of the page, stopping the one it played before: buttons pressed in a row are not heard over each other.</summary>
    private void Play(string sound)
    {
        _playing?.Cancel();
        _playing = new CancellationTokenSource();
        _ = _sounds?.PlayAsync(sound, _playing.Token);
    }

    /// <summary>"Add sound…": the file picked is copied into the pool; a long one is noted, one that is no sound refused (#307).</summary>
    [RelayCommand]
    private void AddSound()
    {
        if (_soundFiles?.Pick() is not { } file)
        {
            return;
        }

        try
        {
            SoundNotice = _library.Add(file).Notice;
        }
        catch (SoundImportException ex)
        {
            SoundNotice = $"This file can't be used as a sound: {ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SoundNotice = $"This file can't be used as a sound: it could not be copied ({ex.Message.TrimEnd('.')}).";
        }
    }

    /// <summary>A sound of the user's, played from its row.</summary>
    [RelayCommand]
    private void PlayUserSound(string id) => Play(id);

    /// <summary>Removes a sound of the user's; the slots that used it go back to their defaults. One that cannot be removed says why.</summary>
    [RelayCommand]
    private void RemoveSound(string id)
    {
        try
        {
            _library.Remove(id); // its slots go back to their defaults as the pool changes (ShowSounds)
            SoundNotice = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SoundNotice = $"This sound could not be removed: {ex.Message.TrimEnd('.')}.";
        }
    }

    /// <summary>The pickers and the list follow the pool, item by item.</summary>
    private void ShowSounds()
    {
        var pool = _library.All;
        List<SoundChoice> wanted = [new(NoSound, "None"), .. pool.Select(s => new SoundChoice(s.Id, s.Name))];
        // A slot whose sound left the pool (its file deleted outside the app) goes back to its default first: taken out of the
        // pickers while picked, its picker would write an empty pick back into it.
        string Kept(string pick, SoundSlot slot) => wanted.Any(c => c.Id == pick) ? pick : ShippedSounds.DefaultFor(slot);
        RavenSoundStart = Kept(RavenSoundStart, SoundSlot.StartsWorking);
        RavenSoundFinish = Kept(RavenSoundFinish, SoundSlot.Finishes);
        RavenSoundQuestion = Kept(RavenSoundQuestion, SoundSlot.AsksQuestion);
        RavenSoundPermission = Kept(RavenSoundPermission, SoundSlot.AsksPermission);
        RavenSoundFail = Kept(RavenSoundFail, SoundSlot.Fails);
        RavenSoundName = Kept(RavenSoundName, SoundSlot.HearsName);
        Follow(SoundChoices, wanted);
        Follow(UserSounds, [.. pool.Where(s => !s.Shipped).Select(s => new UserSoundRow(s.Id, s.Name,
            s.Length.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s"))]);
    }

    /// <summary>Makes <paramref name="shown"/> hold <paramref name="wanted"/> in its order, leaving the items it keeps in place.</summary>
    private static void Follow<T>(ObservableCollection<T> shown, IReadOnlyList<T> wanted)
    {
        for (var i = shown.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(shown[i]))
            {
                shown.RemoveAt(i);
            }
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= shown.Count || !Equals(shown[i], wanted[i]))
            {
                shown.Insert(i, wanted[i]);
            }
        }
    }

    /// <summary>A slot's sound as stored (#306): <see cref="NoSound"/> for none; nothing stored, or a sound no longer in the pool, is the slot's default.</summary>
    private async Task<string> LoadSoundAsync(string key, SoundSlot slot, CancellationToken ct) =>
        await LoadOrDefaultAsync<string>(key, $"the sound of {slot}", ct) is { } stored && (stored == NoSound || _library.Has(stored))
            ? stored : ShippedSounds.DefaultFor(slot);

    /// <summary>The chats' sounds follow the slot's pick at once; it is stored too, once loaded.</summary>
    private void PickSound(SoundSlot slot, string key, string sound)
    {
        _picks.Set(slot, sound == NoSound ? null : sound);
        Persist(key, sound);
    }

    partial void OnRavenSoundStartChanged(string value) => PickSound(SoundSlot.StartsWorking, SettingKeys.RavenSoundStart, value);

    partial void OnRavenSoundFinishChanged(string value) => PickSound(SoundSlot.Finishes, SettingKeys.RavenSoundFinish, value);

    partial void OnRavenSoundQuestionChanged(string value) => PickSound(SoundSlot.AsksQuestion, SettingKeys.RavenSoundQuestion, value);

    partial void OnRavenSoundPermissionChanged(string value) => PickSound(SoundSlot.AsksPermission, SettingKeys.RavenSoundPermission, value);

    partial void OnRavenSoundFailChanged(string value) => PickSound(SoundSlot.Fails, SettingKeys.RavenSoundFail, value);

    partial void OnRavenSoundNameChanged(string value) => PickSound(SoundSlot.HearsName, SettingKeys.RavenSoundName, value);

    /// <summary>Letting go of the voice's slider has the voice picked say its sample at the level just set.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task PlayVoiceLevelAsync() => VoicePage.PlayVoiceLevelAsync();

    partial void OnYardKeepClosedMinutesChanged(int value) => Persist(SettingKeys.YardKeepClosedMinutes, value);

    partial void OnYardHideIdleHoursChanged(int value) => Persist(SettingKeys.YardHideIdleHours, value);

    partial void OnYardMaxChatsChanged(int value) => Persist(SettingKeys.YardMaxChats, value);

    partial void OnRavenMutedWindowsChanged(IReadOnlyList<Guid> value) => Persist(SettingKeys.RavenMutedWindows, value);

    partial void OnRavenCatchUpChanged(bool value) => Persist(SettingKeys.RavenCatchUp, value);

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
        ShowWhisperRows();
    }

    /// <summary>Where Raven's models stand, for the dots beside the engine and the speech-to-text model.</summary>
    public VoiceStatusViewModel Voice { get; }

    /// <summary>The engine picked, or none.</summary>
    public SpeechEngine? Engine => Enum.TryParse<SpeechEngine>(RavenVoiceEngine, out var engine) ? engine : null;

    /// <summary>The model choice is Qwen3-TTS's only.</summary>
    public bool IsQwen => Engine == SpeechEngine.Qwen;

    public bool IsKokoro => Engine == SpeechEngine.Kokoro;

    /// <summary>The voice stored for <paramref name="engine"/>.</summary>
    public string VoiceOf(SpeechEngine engine) => engine == SpeechEngine.Kokoro ? RavenKokoroVoice : RavenQwenVoice;

    /// <summary>An engine and a voice picked on the Voice page: stored, and Raven speaks with them from now on.</summary>
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

    /// <summary>Settings → Voice opens by itself once, with a welcome line: when the Raven panel is first used and no engine is picked.</summary>
    public bool NeedsVoiceSetup => !RavenVoiceSetupShown && Engine is null;

    /// <summary>Downloads a speech-to-text model from its row; the row shows how far it is.</summary>
    private async Task DownloadWhisperAsync(WhisperModel model)
    {
        try
        {
            await _whisper.DownloadAsync(model, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The speech model {Model} could not be downloaded", model);
            LastMessage = $"{ModelLamp.NameOf(model)} could not be downloaded: {ex.Message}";
        }
        finally
        {
            ShowWhisperRows();
        }
    }

    /// <summary>The Qwen3-TTS models its card offers.</summary>
    public static IReadOnlyList<VoiceModelChoice> VoiceModelChoices { get; } =
    [
        new(SpeechModel.Small, "0.6B · faster"),
        new(SpeechModel.Large, "1.7B · richer"),
    ];

    /// <summary>Stored as typed, and not tidied in the box while it is typed in; the brain trims it, and runs the default model for a blank one.</summary>
    partial void OnRavenBrainModelChanged(string value)
    {
        _brain.Model = value;
        Persist(SettingKeys.RavenBrainModel, value);
    }

    /// <summary>Stored as the level the brain runs at: blank for Claude Code's default, and for anything that is no level.</summary>
    partial void OnRavenEffortChanged(string value)
    {
        _brain.Effort = value;
        if (OrDefault(_brain.Effort) is var shown && shown != value)
        {
            RavenEffort = shown; // "extra high" shows as its level, which the picker has; this runs again and stores it
            return;
        }

        Persist(SettingKeys.RavenEffort, _brain.Effort ?? "");
    }

    /// <summary>As <see cref="OnRavenBrainModelChanged"/>, for chat 0.</summary>
    partial void OnRavenOverviewModelChanged(string value)
    {
        _brain.OverviewModel = value;
        Persist(SettingKeys.RavenOverviewModel, value);
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
        if (!_writingAliases)
        {
            ShowAliases();
        }
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
