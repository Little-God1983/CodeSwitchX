using CodeSwitchX.Conductor;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.Ingest.Hooks;
using CodeSwitchX.UI.Cab;
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

    /// <summary>Counts the opens; the status strip belongs to the latest one (see <see cref="ReportFor"/>).</summary>
    private int _openAttempt;

    public ShellViewModel(YardViewModel yard, CabViewModel cab, SettingsViewModel settings, PerformanceBarViewModel performanceBar,
        RavenPanelViewModel raven, ChatSettings chats, HostManager host, ILogger<ShellViewModel> logger)
    {
        _chats = chats;
        Yard = yard;
        Cab = cab;
        Settings = settings;
        PerformanceBar = performanceBar;
        Raven = raven;
        _host = host;
        _logger = logger;
        Yard.OpenRequested += id => _ = EnterCabAsync(id);
        Yard.TileRemoved += OnTileRemoved;
        Yard.HostStopped += OnHostStopped;
        Yard.TilesChanged += () => Raven.SetWorkspaces(Yard.Tiles);
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
        ShowTraffic();
        // The mode is stored as the user chose it: a fall back to push to talk after a failure is not their choice.
        Raven.PreferredMicMode = Enum.TryParse<MicMode>(Settings.RavenMicMode, out var mode) && Enum.IsDefined(mode) ? mode : MicMode.PushToTalk;
        Raven.TileRequested += (_, workspaceId) => ShowTile(workspaceId);
        // As the brain opens a workspace: the window comes forward, from behind VS Code or minimised.
        Raven.CabRequested += (_, workspaceId) => _ = ((IRavenShell)this).OpenInCabAsync(workspaceId);
        _ = Raven.RefreshMicrophonesAsync(); // listed off the UI thread: a slow endpoint must not hold up the first frame
        Raven.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RavenPanelViewModel.IsOpen))
            {
                Settings.RavenPanelOpen = Raven.IsOpen;
                TrackRavenOpen();
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
            else if (e.PropertyName is nameof(SettingsViewModel.RavenCooldownSeconds) or nameof(SettingsViewModel.RavenChatSound)
                or nameof(SettingsViewModel.RavenOwnNewsWaits))
            {
                ShowTraffic();
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
    /// Whether Raven keeps a question it holds (<c>ChatAsks.Keeps</c>): not once the Cab shows the chat's own VS Code,
    /// where the user answers it in the tab. A collapsed panel keeps it: the rail counts it. A minimised window keeps it
    /// too: push-to-talk answers what Raven reads out.
    /// </summary>
    public bool KeepsAsks(Guid? workspaceId)
    {
        lock (_askGate)
        {
            return Keeps(workspaceId);
        }
    }

    /// <summary>Under <see cref="_askGate"/>.</summary>
    private bool Keeps(Guid? workspaceId) => workspaceId is null || workspaceId != _cabShowing;

    /// <summary>The window changed what <see cref="KeepsAsks"/> says. Raised on the UI thread.</summary>
    public event EventHandler? AskRulesChanged;

    /// <summary>The traffic watcher follows the Voice page's cooldown and switches.</summary>
    private void ShowTraffic()
    {
        Raven.Traffic.Cooldown = TimeSpan.FromSeconds(Settings.RavenCooldownSeconds);
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
        Raven.IsListFolded = value == ShellMode.Cab; // VS Code keeps its width: the list folds to its numbers
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

    /// <summary>Brings the window forward, from behind other apps or minimised: what Raven shows by voice is seen.</summary>
    public void BringForward() => ForwardRequested?.Invoke();

    async Task<string?> IRavenShell.OpenInCabAsync(Guid workspaceId)
    {
        if (Yard.FindTile(workspaceId) is null)
        {
            return "it is not on the Yard any more.";
        }

        ForwardRequested?.Invoke();
        await EnterCabAsync(workspaceId);
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

    (string Said, Guid? WorkspaceId)? IRavenShell.SwitchChat(ChatSwitch target) =>
        Raven.SwitchChat(target) is { } chat ? (RavenPanelViewModel.SwitchLine(chat), chat.WorkspaceId) : null;

    void IRavenShell.SetChatDefaults(ChatDefaults defaults) => Settings.SetChatDefaults(defaults);

    void IRavenShell.MarkVoice(string sessionId, string? label) => Yard.MarkVoice(sessionId, label);

    void IRavenShell.ForgetChat(string sessionId) => Yard.ForgetChat(sessionId);

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
        TellHostWhereTheCabWaits();
        if (Mode == ShellMode.Cab && ActiveWorkspaceId is { } id)
        {
            _host.Dock(id, rect);
        }
    }
}
