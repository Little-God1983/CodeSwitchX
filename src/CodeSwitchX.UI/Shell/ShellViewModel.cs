using CodeSwitchX.Conductor;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.Ingest.Hooks;
using CodeSwitchX.UI.Cab;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.UI.Settings;
using CodeSwitchX.UI.Telemetry;
using CodeSwitchX.UI.Yard;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Shell;

/// <summary>
/// Owns the Yard/Cab/Settings mode switch and drives the HostManager for the active workspace. It is also the window
/// Raven's actions act on (<see cref="IRavenShell"/>).
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IRavenShell
{
    private readonly HostManager _host;
    private readonly ChatSettings _chats;
    private readonly IVsCodeChats? _vsCode;
    private readonly ILogger<ShellViewModel> _logger;

    [ObservableProperty]
    private ShellMode _mode = ShellMode.Yard;

    [ObservableProperty]
    private Guid? _activeWorkspaceId;

    [ObservableProperty]
    private string? _statusMessage;

    private bool _shellMinimized;

    /// <summary>
    /// What a chat's question is taken and kept by (<see cref="TakesAsks"/>, <see cref="KeepsAsks"/>), kept for the hook
    /// threads that ask: the panel is open, and the workspace the Cab shows.
    /// </summary>
    private readonly Lock _askGate = new();
    private bool _ravenOpen;
    private Guid? _cabShowing;

    /// <summary>The workspaces with a Raven chat (#148): only their chats' questions are taken. Under <see cref="_askGate"/>.</summary>
    private HashSet<Guid> _tiled = [];

    /// <summary>Counts the opens; the status strip belongs to the latest one (see <see cref="ReportFor"/>).</summary>
    private int _openAttempt;

    /// <summary>Ends the showing of a chat (#115) the user has moved on from: another open, or back to the Yard.</summary>
    private CancellationTokenSource? _chatShow;

    public ShellViewModel(YardViewModel yard, CabViewModel cab, SettingsViewModel settings, PerformanceBarViewModel performanceBar,
        RavenPanelViewModel raven, ChatSettings chats, HostManager host, ILogger<ShellViewModel> logger, IVsCodeChats? vsCode = null)
    {
        _vsCode = vsCode;
        _chats = chats;
        Yard = yard;
        Cab = cab;
        Settings = settings;
        PerformanceBar = performanceBar;
        Raven = raven;
        _host = host;
        _logger = logger;
        Yard.OpenRequested += id => _ = EnterCabAsync(id);
        Yard.OpenChatRequested += (id, chat) => _ = EnterCabAsync(id, chat);
        Yard.TileRemoved += OnTileRemoved;
        Yard.HostStopped += OnHostStopped;
        Yard.TilesChanged += () =>
        {
            Raven.SetWorkspaces(Yard.Tiles);
            TrackTiles();
        };
        Yard.BeforeRemove = MayRemoveAsync;
        Cab.BackRequested += BackToYard;
        Cab.SwitchRequested += id => _ = EnterCabAsync(id);
    }

    /// <summary>The window title: the only place on screen that tells a stable build from a Debug build of the same version.</summary>
    public string Title { get; } = $"CodeSwitchX {AppVersion.Display}";

    public YardViewModel Yard { get; }
    public CabViewModel Cab { get; }
    public SettingsViewModel Settings { get; }
    public PerformanceBarViewModel PerformanceBar { get; }
    public RavenPanelViewModel Raven { get; }

    /// <summary>Raven opens a workspace: the window comes forward, from behind other windows or minimised.</summary>
    public event Action? ForwardRequested;

    /// <summary>
    /// Opens Settings → Voice with its welcome line if the Raven panel is open, no engine is picked, and it never opened
    /// by itself: the first use of the panel. The window calls this once it shows, and the panel's unfolding does.
    /// </summary>
    public void OfferVoiceSetup()
    {
        if (!Raven.IsOpen || !Settings.NeedsVoiceSetup)
        {
            return;
        }

        Settings.RavenVoiceSetupShown = true;
        Settings.VoicePage.ShowWelcome = true;
        OpenSettingsAt(SettingsPage.Voice);
    }

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
        // The Yard owns its tile size and the settings store it. The window shows only after this method, so the tiles
        // are never drawn at the default size first.
        Yard.TileScale = Settings.TileScale;
        ShowYardRules();
        Yard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(YardViewModel.TileScale))
            {
                Settings.TileScale = Yard.TileScale;
            }
        };
        // The same for the Raven panel: it owns its open state and microphone choice, the settings store them. Only the
        // user's choice is stored, never a fallback to the default, so the choice comes back when its device does.
        Raven.IsOpen = Settings.RavenPanelOpen;
        TrackRavenOpen();
        Raven.PreferredMicrophone = Settings.RavenMicrophone;
        Raven.IsMuted = Settings.RavenMuted;
        Raven.SpeakNews = Settings.RavenSpeakNews;
        Raven.BargeIn = Settings.RavenBargeIn;
        Raven.FollowUpSeconds = Settings.RavenFollowUpSeconds;
        Raven.CatchUp = Settings.RavenCatchUp;
        Raven.SetMutedWindows(Settings.RavenMutedWindows);
        Raven.MutedWindowsChanged += (_, _) => Settings.RavenMutedWindows = Raven.MutedWindows;
        ShowTraffic();
        // The mode is stored as the user chose it: a fall back to push to talk after a failure is not their choice.
        Raven.PreferredMicMode = Enum.TryParse<MicMode>(Settings.RavenMicMode, out var mode) && Enum.IsDefined(mode) ? mode : MicMode.PushToTalk;
        Raven.TileRequested += (_, workspaceId) => ShowTile(workspaceId);
        // As the brain opens a workspace: the window comes forward, from behind VS Code or minimised.
        Raven.CabRequested += (_, workspaceId) => _ = ((IRavenShell)this).OpenInCabAsync(workspaceId);
        _ = Raven.RefreshMicrophonesAsync(); // listed off the UI thread: a slow endpoint must not hold up the first frame
        if (Raven.Speakers is { } speakers)
        {
            // The output the same way: only the default picked in Settings is stored, never a trial on the panel.
            speakers.PreferredSpeaker = Settings.RavenSpeaker;
            speakers.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SpeakerChoice.PreferredSpeaker))
                {
                    Settings.RavenSpeaker = speakers.PreferredSpeaker;
                }
            };
            _ = speakers.RefreshAsync();
        }

        Raven.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RavenPanelViewModel.IsOpen))
            {
                Settings.RavenPanelOpen = Raven.IsOpen;
                TrackRavenOpen();
                Fold();
                OfferVoiceSetup();
            }
            else if (e.PropertyName == nameof(RavenPanelViewModel.PreferredMicrophone))
            {
                Settings.RavenMicrophone = Raven.PreferredMicrophone;
            }
            else if (e.PropertyName == nameof(RavenPanelViewModel.IsMuted))
            {
                Settings.RavenMuted = Raven.IsMuted;
            }
            else if (e.PropertyName == nameof(RavenPanelViewModel.PreferredMicMode))
            {
                Settings.RavenMicMode = Raven.PreferredMicMode.ToString();
            }
        };
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.RavenSpeakNews))
            {
                Raven.SpeakNews = Settings.RavenSpeakNews;
            }
            else if (e.PropertyName == nameof(SettingsViewModel.RavenBargeIn))
            {
                Raven.BargeIn = Settings.RavenBargeIn;
            }
            else if (e.PropertyName == nameof(SettingsViewModel.RavenFollowUpSeconds))
            {
                Raven.FollowUpSeconds = Settings.RavenFollowUpSeconds;
            }
            else if (e.PropertyName is nameof(SettingsViewModel.YardKeepClosedMinutes) or nameof(SettingsViewModel.YardHideIdleHours))
            {
                ShowYardRules();
            }
            else if (e.PropertyName is nameof(SettingsViewModel.RavenCooldownSeconds) or nameof(SettingsViewModel.RavenChatSound)
                or nameof(SettingsViewModel.RavenOwnNewsWaits) or nameof(SettingsViewModel.RavenPauseSeconds))
            {
                ShowTraffic();
            }
            else if (e.PropertyName == nameof(SettingsViewModel.RavenCatchUp))
            {
                Raven.CatchUp = Settings.RavenCatchUp;
            }
        };
        // The chips show what a chat Raven starts runs with; Settings holds it, and Raven changes it there by voice.
        ShowChatDefaults();
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.RavenChatModel) or nameof(SettingsViewModel.RavenChatEffort)
                or nameof(SettingsViewModel.RavenModelAliases))
            {
                ShowChatDefaults();
            }
        };
        Raven.ScheduleWarmUp();
        _ = AutoStartAsync();
    }

    private void ShowChatDefaults()
    {
        // What the next chat really starts with: a name the alias table no longer knows starts Claude Code's default.
        Raven.ChatModelChip = _chats.DefaultModelId is { } id ? ChatModels.DisplayName(id) : "Default model";
        Raven.ChatEffortChip = _chats.Defaults.Effort is { } effort ? $"{effort} effort" : "Default effort";
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
    public Task EnterCabAsync(Guid workspaceId) => EnterCabAsync(workspaceId, null);

    /// <param name="chat">A chat's session id, to bring its tab to the front in that VS Code too (#115); null for the workspace alone.</param>
    public async Task EnterCabAsync(Guid workspaceId, string? chat)
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

        LeaveChatShow();
        var showing = chat is null ? null : _chatShow = new CancellationTokenSource();
        ActiveWorkspaceId = workspaceId;
        Raven.ShowChatOf(workspaceId); // the window opened is the one the user talks about
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

            // The workspace shows either way; a chat that cannot be shown is said on the strip.
            if (chat is not null && showing is { IsCancellationRequested: false } && _vsCode is { } vsCode)
            {
                // Off this thread: finding the window reads the companions' records.
                var token = showing.Token;
                await Task.Run(() => vsCode.ShowAsync(tile.Workspace, chat, token), token);
            }
        }
        catch (OperationCanceledException)
        {
            // The user moved on: the chat is not shown behind their back.
        }
        catch (YardActionException ex)
        {
            if (showing is not { IsCancellationRequested: true })
            {
                ReportFor(attempt, ex.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening workspace {Workspace} failed", tile.Name);
            ReportFor(attempt, ex.Message);
        }
    }

    /// <summary>
    /// Tells the host where a new VS Code window of the active workspace goes as it appears: the Cab, while it can show
    /// VS Code (the rule of <see cref="RaiseHostedWindow()"/>). Told on every change of any part of that rule, not asked:
    /// the host's discovery runs on another thread.
    /// </summary>
    private void TellHostWhereTheCabWaits()
    {
        _host.WaitInCab(!_shellMinimized && Mode == ShellMode.Cab && ActiveWorkspaceId is { } id && Cab.LastHostRect is { } rect
            ? (id, rect)
            : null);
        bool changed;
        lock (_askGate)
        {
            var cabShowing = !_shellMinimized && Mode == ShellMode.Cab ? ActiveWorkspaceId : null;
            changed = cabShowing != _cabShowing;
            _cabShowing = cabShowing;
        }

        if (changed)
        {
            AskRulesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The tiles changed: a chat of a workspace removed is on no tile now, and its questions held go to VS Code (#148).
    /// UI thread.
    /// </summary>
    private void TrackTiles()
    {
        HashSet<Guid> tiled = [.. Raven.Chats.Select(c => c.WorkspaceId).OfType<Guid>()]; // as the panel lists them
        bool changed;
        lock (_askGate)
        {
            changed = !tiled.SetEquals(_tiled);
            _tiled = tiled;
        }

        if (changed)
        {
            AskRulesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Whether Raven takes a chat's question (<c>ChatAsks.Takes</c>): while its panel is open and it is kept
    /// (<see cref="KeepsAsks"/>). Asked on the hook's thread.
    /// </summary>
    /// <param name="workspaceId">The chat's workspace on the Yard; null when it is on none.</param>
    public bool TakesAsks(Guid? workspaceId)
    {
        lock (_askGate)
        {
            return _ravenOpen && Keeps(workspaceId);
        }
    }

    /// <summary>
    /// Whether Raven keeps a question it holds (<c>ChatAsks.Keeps</c>): only a chat's on a tile, which has a Raven chat to
    /// read it in; a chat on no tile, its folder never added or its workspace removed, is asked in its VS Code tab (#148).
    /// Not once the Cab shows the chat's own VS Code either, where the user answers it in the tab. A collapsed panel keeps
    /// it: the rail counts it. A minimised window keeps it too: push-to-talk answers what Raven reads out.
    /// </summary>
    public bool KeepsAsks(Guid? workspaceId)
    {
        lock (_askGate)
        {
            return Keeps(workspaceId);
        }
    }

    /// <summary>Under <see cref="_askGate"/>.</summary>
    private bool Keeps(Guid? workspaceId) => workspaceId is { } id && _tiled.Contains(id) && id != _cabShowing;

    /// <summary>The window changed what <see cref="KeepsAsks"/> says. Raised on the UI thread.</summary>
    public event EventHandler? AskRulesChanged;

    /// <summary>The tiles follow the Yard page: how long a closed chat is kept, and when an idle one is hidden (#164).</summary>
    private void ShowYardRules()
    {
        Yard.KeepClosed = TimeSpan.FromMinutes(Settings.YardKeepClosedMinutes);
        Yard.HideIdleAfter = Settings.YardHideIdleHours > 0 ? TimeSpan.FromHours(Settings.YardHideIdleHours) : null;
    }

    /// <summary>The traffic watcher follows the Voice page's cooldown, pause and switches.</summary>
    private void ShowTraffic()
    {
        Raven.Traffic.Cooldown = TimeSpan.FromSeconds(Settings.RavenCooldownSeconds);
        Raven.Traffic.Pause = TimeSpan.FromSeconds(Settings.RavenPauseSeconds);
        Raven.Traffic.SoundOn = Settings.RavenChatSound;
        Raven.Traffic.OwnNewsWaits = Settings.RavenOwnNewsWaits;
    }

    private void TrackRavenOpen()
    {
        lock (_askGate)
        {
            _ravenOpen = Raven.IsOpen;
        }
    }

    partial void OnModeChanged(ShellMode value)
    {
        TellHostWhereTheCabWaits();
        Fold();
    }

    private double? _scrollBar;

    /// <summary>The width of a scroll bar, as the system draws it now (it follows the user's settings); set otherwise in tests.</summary>
    internal double ScrollBar
    {
        get => _scrollBar ?? System.Windows.SystemParameters.VerticalScrollBarWidth;
        set => _scrollBar = value;
    }

    /// <summary>The width of the window's content, as the window last reported it; NaN before it did.</summary>
    private double _width = double.NaN;

    /// <summary>The window's content is this wide now (device-independent pixels): what folds follows it (#162).</summary>
    public void SetWidth(double width)
    {
        _width = width;
        Fold();
    }

    /// <summary>
    /// In the Cab Raven's list folds to its numbers: VS Code keeps its width. In Settings on a narrow window, the list and
    /// then the sidebar fold so the page keeps room to read (<see cref="NarrowLayout"/>).
    /// </summary>
    private void Fold()
    {
        var (list, sidebar) = Mode == ShellMode.Settings ? NarrowLayout.Folds(_width, Raven.IsOpen, ScrollBar) : (false, false);
        Raven.IsListFolded = Mode == ShellMode.Cab || list;
        Settings.IsSidebarFolded = sidebar;
    }

    /// <summary>
    /// Settings left any way (Back to Yard, a workspace opened by Raven or a jump hotkey): a sample playing stops, and the
    /// first-run welcome line has had its turn.
    /// </summary>
    partial void OnModeChanged(ShellMode oldValue, ShellMode newValue)
    {
        if (oldValue == ShellMode.Settings && newValue != ShellMode.Settings)
        {
            Settings.Closed();
        }
    }

    partial void OnActiveWorkspaceIdChanged(Guid? value) => TellHostWhereTheCabWaits();

    private void LeaveChatShow()
    {
        _chatShow?.Cancel();
        _chatShow?.Dispose();
        _chatShow = null;
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

    /// <summary>
    /// Asks the user what becomes of a window's open cards before it is removed (#135): the workspace's name and how many
    /// wait. Set by the main window; null in tests that do not ask, which keeps the workspace.
    /// </summary>
    public Func<string, int, Task<RemoveChoice>>? AskBeforeRemove { get; set; }

    /// <summary>
    /// Whether the workspace may be removed now. With open cards in its Raven chat the user chooses: answer them first (the
    /// panel shows that chat, nothing is removed), leave them to VS Code (then it is removed), or cancel.
    /// </summary>
    private async Task<bool> MayRemoveAsync(Guid workspaceId)
    {
        var cards = Raven.OpenCardsOf(workspaceId);
        if (cards == 0)
        {
            return true;
        }

        var name = Yard.FindTile(workspaceId)?.Name ?? "This workspace";
        if (AskBeforeRemove is not { } ask)
        {
            _logger.LogWarning("Not removing {Workspace}: {Cards} card(s) wait in its Raven chat and there is no window to ask in", name, cards);
            return false;
        }

        var choice = await ask(name, cards);
        switch (choice)
        {
            case RemoveChoice.AnswerFirst:
                Raven.ShowChatOf(workspaceId); // chosen first: opening must not count the chat shown before as seen
                Raven.IsOpen = true; // a folded panel shows no cards
                return false;
            case RemoveChoice.LeaveToVsCode:
                // Once removed, the window's open cards go to VS Code (Raven.SetWorkspaces); a removal that fails keeps them.
                return true;
            default:
                return false;
        }
    }

    /// <summary>The window, set by it once it is made; null before, and in tests that do not need one.</summary>
    public IShellWindow? Window { get; set; }

    string IRavenShell.SetWindow(WindowRequest request)
    {
        if (Window is not { } window)
        {
            throw new YardActionException("CodeSwitchX has no window to change yet: it is still starting.");
        }

        if (request == WindowRequest.Minimize)
        {
            if (window.State == ShellWindowState.Minimized)
            {
                return "CodeSwitchX is already minimized.";
            }

            window.Minimize();
            // Open mic that is paused hears nothing: the key is the way back then too.
            return Raven.MicMode == MicMode.OpenMic && Raven.State != RavenState.AttendingPaused
                ? "CodeSwitchX is minimized. I'm still listening: say \"bring it back\" to see it again."
                : $"CodeSwitchX is minimized. Hold {HotkeyService.PushToTalk.Keys} and say \"bring it back\" to see it again.";
        }

        // Restored from minimized, it comes back as it was before, maximized too, as from the taskbar. Asked to come back
        // while covered, it comes to the front as it is; only asked to restore while in front, a maximized one shrinks.
        // Asked to the front, it never changes size (#222).
        var inFront = window.IsInFront;
        var (state, word) = request == WindowRequest.Maximize ? (ShellWindowState.Maximized, "maximized")
            : window.State == ShellWindowState.Minimized ? (window.Restored, "back")
            : !inFront || request == WindowRequest.Front ? (window.State, "back")
            : (ShellWindowState.Normal, "at its normal size");
        if (window.State == state && inFront)
        {
            return request == WindowRequest.Maximize ? "CodeSwitchX is already maximized." : "CodeSwitchX is already there, in front.";
        }

        window.Show(state);
        return window.IsInFront
            ? $"CodeSwitchX is {word}."
            : $"CodeSwitchX is {word}, but Windows kept another window in front: click it on the taskbar to see it.";
    }

    /// <summary>Brings the window forward, from behind other apps or minimised: what Raven shows by voice is seen.</summary>
    public void BringForward() => ForwardRequested?.Invoke();

    async Task<string?> IRavenShell.OpenInCabAsync(Guid workspaceId)
    {
        if (Yard.FindTile(workspaceId) is null)
        {
            return "it is not on the Yard any more.";
        }

        // Forward once the Cab has switched to it, which it does before it waits for VS Code (and shows a VS Code that runs
        // already): forward before that, the shell's activation raised the VS Code the Cab showed so far, which flashed up
        // first (#224); forward after the wait, nothing showed while VS Code started, and an open that finished late took the
        // keyboard from whatever came since.
        var entering = EnterCabAsync(workspaceId);
        ForwardRequested?.Invoke(); // never throws (MainWindow), so the open is always awaited and said
        await entering;
        return Mode == ShellMode.Cab && ActiveWorkspaceId == workspaceId && StatusMessage is null ? null : StatusMessage ?? "VS Code did not show it.";
    }

    /// <summary>A line of Raven's digest card was clicked: the Yard shows, with the chat's tile lit.</summary>
    internal void ShowTile(Guid workspaceId)
    {
        ((IRavenShell)this).ShowYard();
        Yard.Spotlight(workspaceId);
    }

    void IRavenShell.ShowYard()
    {
        if (Mode != ShellMode.Yard)
        {
            BackToYard();
        }
    }

    string? IRavenShell.NextQuestion() => Raven.NextQuestionForBrain();

    string IRavenShell.WhatsNew(Guid? askedFrom, int? number) => Raven.WhatsNewForBrain(askedFrom, number);

    void IRavenShell.WriteSummary(string? askedIn, string text) => Raven.WriteSummary(askedIn, text);

    string? IRavenShell.MuteChat(int number, bool muted) =>
        Raven.MuteChat(number, muted) is { } chat ? RavenPanelViewModel.MuteLine(chat, Raven.IsMuted) : null;

    (string Said, Guid? WorkspaceId)? IRavenShell.SwitchChat(ChatSwitch target) =>
        Raven.SwitchChat(target) is { } chat ? (RavenPanelViewModel.SwitchLine(chat), chat.WorkspaceId) : null;

    void IRavenShell.SetChatDefaults(ChatDefaults defaults) => Settings.SetChatDefaults(defaults);

    void IRavenShell.MarkVoice(string sessionId, string? label) => Yard.MarkVoice(sessionId, label);

    void IRavenShell.ForgetChat(string sessionId) => Yard.ForgetChat(sessionId);

    void IRavenShell.Tell(Guid workspaceId, string text, bool failed) => Raven.Tell(workspaceId, text, failed);

    void IRavenShell.FollowWork(string askedIn, Guid workspaceId) => Raven.FollowWork(askedIn, workspaceId);

    [RelayCommand]
    public void BackToYard()
    {
        LeaveChatShow();
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

    /// <summary>Opens workspace number <paramref name="number"/> in the Cab, wherever its tile is; nothing when no workspace has it.</summary>
    [RelayCommand]
    public async Task JumpToAsync(int number)
    {
        if (number > 0 && Yard.Tiles.FirstOrDefault(t => t.Number == number) is { } tile)
        {
            await EnterCabAsync(tile.Id);
        }
    }

    [RelayCommand]
    public void OpenSettings()
    {
        _host.HideAll();
        Settings.Refresh();
        Mode = ShellMode.Settings;
    }

    /// <summary>Opens Settings on <paramref name="page"/>: a model's dot on the bottom bar, the New chats chips on Raven's panel.</summary>
    [RelayCommand]
    public void OpenSettingsAt(SettingsPage page)
    {
        Settings.OpenPage(page);
        OpenSettings();
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
        TellHostWhereTheCabWaits();
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
    /// minimised, with a known Cab rectangle. The one place for that rule: an open that finishes, a restore, and a
    /// keyboard activation of the shell all come here. A click on the shell tucks the shell under VS Code instead, as
    /// Windows leaves VS Code under the shell then (#213, <see cref="CodeSwitchX.Hosting.Win32.ZOrder.TuckUnder"/>).
    /// </summary>
    public void RaiseHostedWindow() => RaiseHostedWindow(focus: true);

    /// <param name="focus">False puts VS Code on top without the foreground: a click on the shell that activated it is still going on.</param>
    public void RaiseHostedWindow(bool focus)
    {
        if (!_shellMinimized && Mode == ShellMode.Cab && ActiveWorkspaceId is { } id && Cab.LastHostRect is { } rect)
        {
            // A dialog of the shell's up (Add workspace): VS Code comes in right under it, and the dialog keeps the foreground (#215).
            _host.ShowInCab(id, rect, focus, under: Window?.Dialog ?? 0);
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
        TellHostWhereTheCabWaits();
        if (Mode == ShellMode.Cab && ActiveWorkspaceId is { } id)
        {
            // A window shown here for the first time comes in under a dialog of the shell's, as in RaiseHostedWindow (#215).
            _host.Dock(id, rect, under: Window?.Dialog ?? 0);
        }
    }
}
