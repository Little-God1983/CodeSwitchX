using System.Collections.ObjectModel;
using System.IO;
using CodeSwitchX.Core.GitHub;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Paths;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.GitHub;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Raven;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Yard;

/// <summary>The tile board: workspace tiles, each with live chat rows, in groups by when their chats were last used (#276).</summary>
public sealed partial class YardViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan GitRefreshInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan GitHubRefreshInterval = TimeSpan.FromMinutes(10);

    private readonly IWorkspaceStore _store;
    private readonly WorkspaceRegistry _registry;
    private readonly SessionEngine _engine;
    private readonly IPricingProvider _pricing;
    private readonly GitInspector _git;
    private readonly IEventBus _bus;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly ILogger<YardViewModel> _logger;
    private readonly IVsCodeOpenTabs? _openTabs;
    private readonly Func<string, TabConversation?> _writtenInAt;
    private readonly Func<IReadOnlyDictionary<string, bool>> _runningTabs;
    private readonly Func<CancellationToken, Task<IReadOnlyDictionary<Guid, DateTimeOffset>>> _storedLastUse;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Lock _gitGate = new();
    private ITimer? _tickTimer;
    private ITimer? _gitTimer;
    private ITimer? _gitHubTimer;
    private readonly GitHubActivity? _gitHub;
    private readonly GitHubReader? _gitHubReader;
    private readonly GitHubBrowsers? _gitHubBrowsers;
    private readonly ITextClipboard _clipboard;
    private readonly CancellationTokenSource _gitHubStop = new();
    private readonly Dictionary<string, object> _gitHubAsked = new(StringComparer.OrdinalIgnoreCase);
    private ITimer? _tabsTimer;

    /// <summary>The looks a moment after a VS Code closed (<see cref="ClosedListWait"/>), one per close; UI thread.</summary>
    private readonly HashSet<ITimer> _closedListTimers = [];

    private bool _disposed;

    /// <summary>Ends with the read of the tabs that runs; null while none does. Under <see cref="_tabsGate"/>.</summary>
    private TaskCompletionSource? _tabsRead;

    /// <summary>Whether <see cref="_tabsRead"/> has begun to take the list: a look asked before that is covered by it. Under <see cref="_tabsGate"/>.</summary>
    private bool _tabsReadBegun;

    /// <summary>Ends with the read after it, asked for while it ran; null while none was. Under <see cref="_tabsGate"/>.</summary>
    private TaskCompletionSource? _tabsReadNext;

    /// <summary>
    /// Whether a read runs and whether another was asked for are decided under one lock, as for git: in the app it all
    /// runs on the UI thread, but in tests a read goes on on the thread pool while the next look is asked for.
    /// </summary>
    private readonly Lock _tabsGate = new();

    /// <summary>When the chats of tabs the app knows no chat of were last written in; looked up once a chat (UI thread).</summary>
    private readonly Dictionary<string, TabConversation> _tabActivity = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// When each such chat was last asked about: it is asked about again a minute on, as its title and last time change
    /// while it runs, and one not known yet gets its conversation with its first message.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _tabActivityAsked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long until a tab whose chat was not found on disk is looked for again: a new tab's chat is written with its first message.</summary>
    internal static readonly TimeSpan AskAgainAfter = TimeSpan.FromMinutes(1);

    /// <summary>How long a closed chat can be kept on its tile, in minutes; 0 is not at all.</summary>
    public static readonly IReadOnlyList<int> KeepClosedMinutesChoices = [0, 1, 5, 10, 30];

    /// <summary>How long a chat can be idle before it is hidden, in hours; 0 is never.</summary>
    public static readonly IReadOnlyList<int> HideIdleHoursChoices = [0, 1, 4, 12, 24];

    /// <summary>How often VS Code's lists of open chat tabs are looked at; a list is read again only when its file changed.</summary>
    public static readonly TimeSpan TabsInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long after a stop the tabs are read again: VS Code writes the list it comes back with as its window goes, within
    /// about 50 ms of it on this machine (measured 2026-10-10), so the look a stop sets off may take the list before that (#248).
    /// </summary>
    internal static readonly TimeSpan ClosedListWait = TimeSpan.FromSeconds(1);

    private TimeSpan _keepClosed;
    private TimeSpan? _hideIdleAfter;

    /// <summary>How long a chat stays on its tile after its tab was closed, greyed (#164); zero for not at all. The shell sets it from Settings.</summary>
    public TimeSpan KeepClosed
    {
        get => _keepClosed;
        set
        {
            _keepClosed = value;
            Rearrange();
        }
    }

    /// <summary>How long a chat may be idle before its row is hidden, tab or not (#164); null for never. The shell sets it from Settings.</summary>
    public TimeSpan? HideIdleAfter
    {
        get => _hideIdleAfter;
        set
        {
            _hideIdleAfter = value;
            Rearrange();
        }
    }

    /// <summary>The voice label of a chat Raven started, null for any other: a row gets it as it is made.</summary>
    internal string? VoiceLabelOf(string sessionId) => _voiceLabels.GetValueOrDefault(sessionId);

    /// <summary>
    /// Whether the engine put the chat on another tile than this one (the folder it runs in is that tile's): it is that
    /// tile's to show, by what the chat reports, and a tab of it here is not a second row.
    /// </summary>
    internal bool OnAnotherTile(WorkspaceTileViewModel tile, string sessionId) => Tiles.Any(t => t != tile && t.Knows(sessionId));

    /// <summary>
    /// Whether the chat was closed on purpose and has not run since: its tab, still in a list VS Code has not written
    /// again, brings no row back.
    /// </summary>
    internal bool ClosedOnPurpose(string sessionId) => _closed.ContainsKey(sessionId);

    /// <summary>The read of the tabs that runs, or the last one; tests await it.</summary>
    internal Task CurrentTabsRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>The time the tiles go by.</summary>
    internal DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>What a chat row's right-click does (#386); null where the rows have no menu.</summary>
    internal IChatRowMenu? RowMenu { get; }

    /// <summary>What the tiles' Build new version / Run entries start (#423).</summary>
    internal IStableBuildLauncher StableBuilds { get; }

    /// <summary>The Yard's questions to the user, the build's "are you sure" among them (#423).</summary>
    internal IYardDialogs Dialogs { get; }

    private void Rearrange()
    {
        var now = Now;
        foreach (var tile in Tiles.ToList())
        {
            tile.Tick(now);
        }
    }
    private Task _gitRefresh = Task.CompletedTask;
    private bool _gitRefreshRunning;
    private bool _gitRefreshAgain;

    /// <summary>How long a git round waits for the UI thread to hand over the tiles; a thread that never answers (the dispatcher has shut down) must not keep every later round from running.</summary>
    internal TimeSpan UiTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The git round that runs, or the last one; tests await it.</summary>
    internal Task CurrentGitRefresh => _gitRefresh;

    [ObservableProperty] private bool _needsMeFirst;

    /// <summary>How long the tiles keep their places after the pointer last moved over the board (#276 review).</summary>
    internal static readonly TimeSpan HoldOrderFor = TimeSpan.FromSeconds(2);

    /// <summary>Until when the tiles keep their places: the pointer moves over the board (UI thread).</summary>
    private DateTimeOffset _holdOrderUntil = DateTimeOffset.MinValue;

    /// <summary>A tile's context menu is open: the tiles keep their places until it closes, however long the user reads it.</summary>
    private bool _menuOpen;

    /// <summary>A tile's context menu opened or closed; closed, what changed meanwhile is sorted in.</summary>
    public void MenuOpen(bool open)
    {
        _menuOpen = open;
        if (!open)
        {
            HoldOrder(); // the pointer is still over the board, on the item it clicked
            Resort();
        }
    }

    /// <summary>
    /// The pointer moved over the board: the tiles keep their places for <see cref="HoldOrderFor"/>, or one would slide
    /// under the click aimed at another as a chat starts somewhere. The tick sorts in what changed meanwhile; a pointer
    /// resting on the board holds nothing.
    /// </summary>
    public void HoldOrder() => _holdOrderUntil = Now + HoldOrderFor;

    /// <summary>The pointer left the board: what changed while it was there is sorted in now.</summary>
    public void ReleaseOrder()
    {
        _holdOrderUntil = DateTimeOffset.MinValue;
        Rearrange(); // the tiles' rows too (#295)
        Resort();
    }

    /// <summary>The pointer or a context menu holds the tiles and their rows where they are (<see cref="HoldOrder"/>).</summary>
    internal bool OrderHeld => _menuOpen || Now < _holdOrderUntil;
    [ObservableProperty] private bool _hooksInferredOnly;

    /// <summary>
    /// What the installer says (set by the shell from Settings). The "Hooks not installed" banner shows only while it says
    /// no: a chat that ran before Install hooks was clicked stays inferred until it sends a hook, and must not keep the
    /// banner up meanwhile.
    /// </summary>
    public bool HooksInstalled { get; set; }

    public const double MinTileScale = 0.75;
    public const double MaxTileScale = 1.5;
    private double _tileScale = 1;

    /// <summary>
    /// How big the tiles are drawn: 1 is their normal size. The slider in the header sets it; the shell hands it the
    /// stored value at startup and stores every change. A value outside the slider's range (a stored one edited by hand,
    /// or from a build with another range) is pulled into it, and it is kept to two decimals: the slider's snapping adds
    /// float noise (0.9000000000000001) that would otherwise be stored.
    /// </summary>
    public double TileScale
    {
        get => _tileScale;
        set
        {
            if (SetProperty(ref _tileScale, Slid(value, MinTileScale, MaxTileScale)))
            {
                OnPropertyChanged(nameof(TileWidth));
                OnPropertyChanged(nameof(TileMinHeight));
            }
        }
    }

    /// <summary>A tile's width at <see cref="TileScale"/> 1.</summary>
    public const double TileBaseWidth = 300;

    /// <summary>A tile's least height at <see cref="TileScale"/> 1.</summary>
    public const double TileBaseMinHeight = 150;

    /// <summary>A tile's width: the tile size sizes the tile, not its text (#295), so a wider one shows more of a title.</summary>
    public double TileWidth => TileBaseWidth * TileScale;

    public double TileMinHeight => TileBaseMinHeight * TileScale;

    public const double MinFontScale = 0.75;
    public const double MaxFontScale = 1.5;
    private double _fontScale = 1;

    /// <summary>
    /// How big the tiles' text is drawn, apart from the tile size (#295): 1 is its normal size. The slider in the header sets
    /// it, and the shell stores it as it does the tile size; kept in the slider's range and to two decimals the same way.
    /// </summary>
    public double FontScale
    {
        get => _fontScale;
        set => SetProperty(ref _fontScale, Slid(value, MinFontScale, MaxFontScale));
    }

    private static double Slid(double value, double min, double max) => double.IsFinite(value) ? Math.Round(Math.Clamp(value, min, max), 2) : 1;

    public const int MinMaxChats = 4;
    public const int MaxMaxChats = 10;
    public const int DefaultMaxChats = 6;

    /// <summary>The most chats a tile can be set to show, as the Yard page offers them.</summary>
    public static readonly IReadOnlyList<int> MaxChatsChoices = [.. Enumerable.Range(MinMaxChats, MaxMaxChats - MinMaxChats + 1)];

    private int _maxChats = DefaultMaxChats;

    /// <summary>
    /// The most chat rows a tile shows (#295); the rest are a "+N more" line. The shell sets it from Settings; a value outside
    /// <see cref="MinMaxChats"/>..<see cref="MaxMaxChats"/> is pulled into it.
    /// </summary>
    public int MaxChats
    {
        get => _maxChats;
        set
        {
            _maxChats = Math.Clamp(value, MinMaxChats, MaxMaxChats);
            Rearrange();
        }
    }

    public YardViewModel(IWorkspaceStore store, WorkspaceRegistry registry, SessionEngine engine, IPricingProvider pricing, GitInspector git,
        IEventBus bus, IUiDispatcher ui, TimeProvider time, ILogger<YardViewModel> logger, IVsCodeOpenTabs? openTabs = null,
        Func<string, TabConversation?>? writtenInAt = null, Func<IReadOnlyDictionary<string, bool>>? runningTabs = null,
        Func<CancellationToken, Task<IReadOnlyDictionary<Guid, DateTimeOffset>>>? storedLastUse = null, GitHubActivity? gitHub = null,
        GitHubReader? gitHubReader = null, GitHubBrowsers? gitHubBrowsers = null, ITextClipboard? clipboard = null, IChatRowMenu? rowMenu = null,
        GitPuller? puller = null, GitHubMerger? merger = null, IStableBuildLauncher? stableBuilds = null, IYardDialogs? dialogs = null)
    {
        RowMenu = rowMenu;
        StableBuilds = stableBuilds ?? new StableBuildLauncher();
        Dialogs = dialogs ?? new YardDialogs();
        GitJobs = new YardGitJobs(puller ?? new GitPuller(), ui, time, logger, merger);
        GitJobs.Ended += OnGitJobEnded;
        _gitHub = gitHub;
        _gitHubReader = gitHubReader;
        _gitHubBrowsers = gitHubBrowsers;
        _clipboard = clipboard ?? new WpfTextClipboard();
        _storedLastUse = storedLastUse ?? (_ => Task.FromResult<IReadOnlyDictionary<Guid, DateTimeOffset>>(new Dictionary<Guid, DateTimeOffset>()));
        _runningTabs = runningTabs ?? (() => new Dictionary<string, bool>());
        _openTabs = openTabs;
        _writtenInAt = writtenInAt ?? (_ => null);
        _store = store;
        _registry = registry;
        _engine = engine;
        _pricing = pricing;
        _git = git;
        _bus = bus;
        _ui = ui;
        _time = time;
        _logger = logger;
    }

    /// <summary>Now &amp; today, This week and Older, in that order and always all three; the view hides one without tiles.</summary>
    public IReadOnlyList<LastUseGroupViewModel> Groups { get; } = [new(LastUse.Today), new(LastUse.ThisWeek), new(LastUse.Older)];

    public IEnumerable<WorkspaceTileViewModel> Tiles => Groups.SelectMany(g => g.Tiles);

    /// <summary>The name of each track, by its id (UI thread): the board no longer groups by track, the MCP answers still name it.</summary>
    private readonly Dictionary<Guid, string> _trackNames = [];

    /// <summary>The name of the track, or "Track" while a new one's name is being read.</summary>
    public string TrackNameOf(Guid trackId) => _trackNames.GetValueOrDefault(trackId, "Track");

    private LastUseGroupViewModel GroupOf(LastUse group) => Groups[(int)group];

    public event Action<Guid>? OpenRequested;

    /// <summary>A chat's row on a tile was clicked (#115): the workspace opens with that chat, given by its session id, in front.</summary>
    public event Action<Guid, string>? OpenChatRequested;
    /// <summary>Open the Add workspace dialog; with a path (a file or folder dropped on the Yard), detect that path at once.</summary>
    public event Action<string?>? AddWorkspaceRequested;

    /// <summary>A tile left the board (its workspace was unregistered): the Cab cannot show it any more.</summary>
    public event Action<Guid>? TileRemoved;

    /// <summary>The tiles came in, or one was added or removed: Raven's chat list follows them (UI thread).</summary>
    public event Action? TilesChanged;

    /// <summary>A tile's VS Code window, open until now, is gone: closed by the user, or taken over by another folder.</summary>
    public event Action<Guid>? HostStopped;

    public async Task InitializeAsync(CancellationToken ct)
    {
        var tracks = await _store.GetTracksAsync(ct);
        var workspaces = await _store.GetAllAsync(ct);
        _trackNames.Clear();
        foreach (var track in tracks)
        {
            _trackNames[track.Id] = track.Name;
        }

        foreach (var group in Groups)
        {
            group.Tiles.Clear();
        }

        foreach (var workspace in workspaces)
        {
            GroupOf(LastUse.Older).Tiles.Add(new WorkspaceTileViewModel(workspace, this));
        }

        await ReadStoredUseAsync(ct);
        Resort();
        TilesChanged?.Invoke();

        // Subscribe first, then read the current snapshots: a change published in between would otherwise be lost
        // (applying a snapshot twice is harmless).
        _subscriptions.Add(_bus.Subscribe<SessionChanged>(m => _ui.Post(() => Apply(m.Current))));
        _subscriptions.Add(_bus.Subscribe<WorkspaceRegistered>(m => _ui.Post(() => AddTile(m.Workspace))));
        _subscriptions.Add(_bus.Subscribe<WorkspaceUnregistered>(m => _ui.Post(() => RemoveTile(m.WorkspaceId))));
        _subscriptions.Add(_bus.Subscribe<HostStateChanged>(m => _ui.Post(() => Apply(m))));
        foreach (var snapshot in _engine.Snapshots)
        {
            Apply(snapshot);
        }

        if (_openTabs is not null)
        {
            // Read before the window shows: the tiles are never drawn without the tabs VS Code comes back with.
            await RefreshTabsAsync();
            if (_disposed)
            {
                return; // closed during the read: no timer is made that nothing would dispose
            }

            _tabsTimer = _time.CreateTimer(_ => _ui.Post(() => CurrentTabsRefresh = RefreshTabsAsync(again: false)), null, TabsInterval, TabsInterval);
        }

        _tickTimer = _time.CreateTimer(_ => _ui.Post(() => Tick(_time.GetUtcNow())), null, TickInterval, TickInterval);
        _ = RefreshGitAsync(CancellationToken.None);
        // The tick asks for nothing while a round runs: a round longer than the interval would otherwise repeat back to back.
        _gitTimer = _time.CreateTimer(_ => _ = RequestGitRefresh(CancellationToken.None, again: false), null, GitRefreshInterval, GitRefreshInterval);
        if (_gitHub is not null)
        {
            _gitHub.Changed += OnGitHubChanged;
            _gitHubTimer = _time.CreateTimer(_ => _ui.Post(() => _ = RefreshGitHubOfAllTilesAsync(pressed: false)), null, GitHubRefreshInterval, GitHubRefreshInterval);
        }
    }

    public WorkspaceTileViewModel? FindTile(Guid workspaceId) => Tiles.FirstOrDefault(t => t.Id == workspaceId);

    /// <summary>
    /// When the chats of each window were last used, as stored (<see cref="ISessionStore.GetLastUseByWorkspaceAsync"/>): the
    /// engine holds only the last day's chats. The same use as the tiles see live (#278). A failed read leaves the tiles to
    /// what the engine knows.
    /// </summary>
    private async Task ReadStoredUseAsync(CancellationToken ct)
    {
        try
        {
            var lastUse = await _storedLastUse(ct);
            foreach (var tile in Tiles)
            {
                if (lastUse.TryGetValue(tile.Id, out var at))
                {
                    tile.SawUse(at);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reading when the chats of the workspaces were last used failed; the Yard groups go by the chats of the last day");
        }
    }

    /// <summary>The voice label of each chat Raven started (UI thread); a row that comes later gets it as it is added.</summary>
    private readonly Dictionary<string, string> _voiceLabels = new(StringComparer.Ordinal);

    /// <summary>
    /// Marks a chat as one Raven started, with the model and effort it started with; null takes the mark off. Its row may
    /// not be there yet: the chat shows once its hooks report it.
    /// </summary>
    public void MarkVoice(string sessionId, string? label)
    {
        if (label is null)
        {
            _voiceLabels.Remove(sessionId);
        }
        else
        {
            _voiceLabels[sessionId] = label;
        }

        foreach (var row in Tiles.SelectMany(t => t.Chats).Where(c => c.SessionId == sessionId))
        {
            row.VoiceLabel = label;
        }
    }

    /// <summary>When each chat closed on purpose was closed (UI thread): it keeps no row until it is opened again.</summary>
    private readonly Dictionary<string, DateTimeOffset> _closed = new(StringComparer.Ordinal);

    /// <summary>
    /// Takes a chat that was closed on purpose off its tile now, rather than as an ended row 10 minutes from now. What is
    /// still heard of its end brings no row back; only a change after this, of a chat that runs again (opened again from
    /// VS Code's session list), does.
    /// </summary>
    public void ForgetChat(string sessionId)
    {
        _closed[sessionId] = _time.GetUtcNow();
        foreach (var tile in Tiles.ToList())
        {
            tile.Remove(sessionId);
        }
    }

    /// <summary>
    /// Whether the snapshot is of a chat closed on purpose that has not run since, and gets no row. One that runs again
    /// after the close is forgotten no more.
    /// </summary>
    private bool IsClosed(SessionSnapshot snapshot)
    {
        if (!_closed.TryGetValue(snapshot.SessionId, out var closed))
        {
            return false;
        }

        if (!SessionStateMachine.IsLive(snapshot.State) || snapshot.LastEventAt <= closed)
        {
            return true;
        }

        _closed.Remove(snapshot.SessionId);
        return false;
    }

    /// <summary>
    /// Reads the chat tabs open in the workspaces' VS Code windows, off the UI thread, and shows them on the tiles (#164).
    /// Called on the UI thread. Asked for while a read runs, it gives the read after it: VS Code may have written its list
    /// since the read took it (as it closes, #238). However often it is asked meanwhile, that one more read covers it, and
    /// the task ends with the read that covers the look, not with the looks asked after it (#248).
    /// </summary>
    /// <param name="again">False for the timer's look: the read that runs is enough, or reads slower than the timer would
    /// run back to back.</param>
    internal Task RefreshTabsAsync(bool again = true)
    {
        if (_openTabs is not { } openTabs || _disposed)
        {
            return Task.CompletedTask;
        }

        lock (_tabsGate)
        {
            if (_tabsRead is { } running)
            {
                return again && _tabsReadBegun ? (_tabsReadNext ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task
                    : running.Task;
            }

            _tabsRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _tabsReadBegun = false;
            _ = ReadTabsUntilAskedAsync(openTabs);
            return _tabsRead.Task;
        }
    }

    /// <summary>Reads the tabs, and once more for as long as a look was asked for while it read.</summary>
    private async Task ReadTabsUntilAskedAsync(IVsCodeOpenTabs openTabs)
    {
        await Task.Yield(); // out of the lock it was started in: none of the read runs under it
        try
        {
            while (true)
            {
                lock (_tabsGate)
                {
                    _tabsReadBegun = true;
                }

                await ReadTabsAsync(openTabs); // never throws: a failure is logged

                TaskCompletionSource read;
                TaskCompletionSource? next;
                bool more;
                lock (_tabsGate)
                {
                    // Decided in one critical section with the asking, so no look is lost.
                    read = _tabsRead!;
                    _tabsRead = _disposed ? null : _tabsReadNext; // disposed, the read asked for is not made
                    next = _disposed ? _tabsReadNext : null;
                    _tabsReadNext = null;
                    _tabsReadBegun = false;
                    more = _tabsRead is not null;
                }

                read.SetResult();
                next?.SetResult();
                if (!more)
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // Not foreseen: the looks waiting fail rather than wait for good, and the next look reads afresh.
            TaskCompletionSource? read, next;
            lock (_tabsGate)
            {
                (read, next) = (_tabsRead, _tabsReadNext);
                (_tabsRead, _tabsReadNext, _tabsReadBegun) = (null, null, false);
            }

            read?.TrySetException(ex);
            next?.TrySetException(ex);
        }
    }

    /// <summary>One read of the tabs onto the tiles; never throws: a failure is logged, and the tiles keep what they show.</summary>
    private async Task ReadTabsAsync(IVsCodeOpenTabs openTabs)
    {
        try
        {
            var workspaces = Tiles.Select(t => t.Workspace).ToList();
            var now = Now;
            var asked = _tabActivityAsked.Where(a => now - a.Value < AskAgainAfter).Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var (tabs, activity, running) = await Task.Run(() =>
            {
                var read = openTabs.Read(workspaces);
                // Only for tabs of chats the app has no state of: theirs is the last thing known of them.
                var unknown = read.Values.SelectMany(t => t.Tabs).Select(t => t.SessionId).Where(id => _engine.Get(id) is null).ToList();
                var runs = unknown.Count == 0 ? new Dictionary<string, bool>() : _runningTabs();
                var written = new Dictionary<string, TabConversation?>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in unknown.Where(id => !asked.Contains(id)))
                {
                    written[id] = _writtenInAt(id);
                }

                return (read, written, runs);
            });

            foreach (var (id, at) in activity)
            {
                // One that is not known yet (a new tab has no conversation, a folder could not be read) is asked for again later.
                _tabActivityAsked[id] = now;
                if (at is { } known)
                {
                    _tabActivity[id] = known;
                }
                else
                {
                    _tabActivity.Remove(id);
                }
            }

            // A tile added while the read ran was not asked about: it keeps what it has until the next read.
            foreach (var tile in Tiles.Where(t => workspaces.Exists(w => w.Id == t.Id)).ToList())
            {
                tile.ShowTabs(tabs.GetValueOrDefault(tile.Id), _tabActivity, running);
            }

            Resort();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading VS Code's open chat tabs failed; the tiles keep the ones read before");
        }
    }

    public void RequestOpen(Guid workspaceId) => OpenRequested?.Invoke(workspaceId);

    /// <summary>The workspace the Cab shows the user (#287), null while it shows none (the Yard, Settings, minimised). UI thread.</summary>
    private Guid? _cabShowing;

    /// <summary>The Cab shows the workspace now, or none: what finished there is seen (#287). UI thread.</summary>
    public void CabShows(Guid? workspaceId)
    {
        _cabShowing = workspaceId;
        if (workspaceId is { } id)
        {
            FindTile(id)?.Seen();
        }
    }

    /// <summary>Whether the Cab shows the workspace to the user right now (#287).</summary>
    internal bool InCab(Guid workspaceId) => _cabShowing == workspaceId;

    public void RequestOpenChat(Guid workspaceId, string sessionId) => OpenChatRequested?.Invoke(workspaceId, sessionId);

    /// <summary>
    /// Asked before a workspace is removed, with its id: false keeps it (#135: the shell asks what becomes of the cards in
    /// its Raven chat). Null removes at once.
    /// </summary>
    public Func<Guid, Task<bool>>? BeforeRemove { get; set; }

    public async Task UnregisterAsync(Guid workspaceId)
    {
        try
        {
            if (BeforeRemove is { } ask && !await ask(workspaceId))
            {
                return;
            }

            await _registry.UnregisterAsync(workspaceId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unregistering workspace {Id} failed", workspaceId);
        }
    }

    public void Tick(DateTimeOffset now)
    {
        foreach (var tile in Tiles.ToList())
        {
            tile.Tick(now);
        }

        // A hook-fed chat proves the hooks work wherever it is shown, including on no tile; so does the installer.
        HooksInferredOnly = !HooksInstalled && Tiles.Any(t => t.HasInferredChats) && !_engine.Snapshots.Any(s => !s.Inferred && SessionStateMachine.IsLive(s.State));
        Resort(); // a tile leaves Now & today a day after its last use
    }

    /// <summary>
    /// Refreshes every tile's git facts and the worktrees of every repository workspace. One round runs at a time; a
    /// refresh asked for while one runs (a workspace added meanwhile, the Refresh command) makes it run once more when it
    /// ends, and the task returned to either caller ends with that. A tile whose check fails is logged, and the others go on.
    /// </summary>
    public Task RefreshGitAsync(CancellationToken ct) => RequestGitRefresh(ct, again: true);

    /// <summary>A tile's menu opens with Build new version on it (#425): <see cref="ReadGitOfAsync"/>, not waited for.</summary>
    public void RefreshGitOf(WorkspaceTileViewModel tile) => _ = ReadGitOfAsync(tile);

    /// <summary>
    /// Reads one tile's git now, beside any round that runs (#425): a tile's menu opens, and its Build entries must not go by lines up to a
    /// round old (a branch switched a moment ago). Only its git: no worktree list, no GitHub. The tile shows the lines; a round that started
    /// before this read cannot put older ones back. A read already under way for the tile is the one (open, Esc, open). UI thread.
    /// </summary>
    /// <returns>The lines the read found; null when it failed or the Yard closed.</returns>
    internal Task<IReadOnlyList<GitLine>?> ReadGitOfAsync(WorkspaceTileViewModel tile)
    {
        if (_disposed)
        {
            return Task.FromResult<IReadOnlyList<GitLine>?>(null);
        }

        if (tile.MenuGitRead is { IsCompleted: false } running)
        {
            return running;
        }

        var stop = _gitHubStop.Token; // the Yard's own: closing it stops the read
        var read = Task.Run<IReadOnlyList<GitLine>?>(async () =>
        {
            try
            {
                return await RefreshGitAsync(tile, [], stop, worktrees: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Git refresh of {Root} for its menu failed", tile.RootPath);
                return null;
            }
        });
        tile.MenuGitRead = read;
        return read;
    }

    /// <summary>How long a Build press waits for its read of GitHub (#429) before it goes by the last one.</summary>
    internal static readonly TimeSpan BuildGitHubWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// <paramref name="line"/>'s repository as it is now, for a Build pressed (#425): its git, its scripts and how far it is behind, read
    /// for that one repository and nothing else on the tile, so the answer comes quickly and from after the press. Where it stands on
    /// GitHub is read too, beside git (#429): a merge made outside the Yard (on github.com, by a chat) would otherwise be up to ten minutes
    /// away, and the build start only for its script to refuse. Null when git could not be read, or the Yard closed.
    /// </summary>
    internal async Task<GitLine?> FreshLineAsync(GitLine line)
    {
        if (_disposed || line.StableBuild is not { } scripts)
        {
            return null;
        }

        try
        {
            // A GitHub read that fails leaves what was last read, which the comparison then goes by, as the round does.
            var gitHubRead = _gitHub is not null && line is { IsRepository: true, Path: { } path } ? RefreshGitHubAsync([path]) : Task.FromResult(default(GitHubRoundResult));
            var info = await _git.InspectAsync(scripts.RepoPath, _gitHubStop.Token).ConfigureAwait(false);
            try
            {
                // It queues behind a GitHub round that runs, and gh may take its time: the press goes by the last read rather than wait long.
                await gitHubRead.WaitAsync(BuildGitHubWait, _time, _gitHubStop.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogInformation("GitHub did not answer within {Wait} for a build; its last read stands", BuildGitHubWait);
            }
            // With what GitHub has now, as the comparison goes by: the line's own is the one it was shown with.
            var fresh = WithGitHub(line with
            {
                Branch = info.Branch, DirtyCount = info.DirtyCount, Head = info.Head, IsRepository = info.IsRepository, GitDir = info.GitDir,
                StableBuild = StableBuildOf(info, [line]), TrackedChanges = info.TrackedChanges,
            });
            return (await WithBehindAsync([fresh], [line], _gitHubStop.Token).ConfigureAwait(false))[0];
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading {Folder} for its build failed", scripts.RepoPath);
            return null;
        }
    }

    /// <summary>Whether a round runs and whether another was asked for are decided under one lock, so a request never falls between a round's last look and its end.</summary>
    private Task RequestGitRefresh(CancellationToken ct, bool again)
    {
        lock (_gitGate)
        {
            if (_gitRefreshRunning)
            {
                _gitRefreshAgain |= again;
                return _gitRefresh;
            }

            _gitRefreshRunning = true;
            _gitRefreshAgain = false;
            _gitRefresh = RefreshGitLoopAsync(ct);
            return _gitRefresh;
        }
    }

    private async Task RefreshGitLoopAsync(CancellationToken ct)
    {
        try
        {
            do
            {
                var tiles = await TilesOnUiThreadAsync().ConfigureAwait(false);
                var pathsOf = new Dictionary<WorkspaceTileViewModel, List<string>>();
                var shownBefore = tiles.ToDictionary(t => t, t => ShownFolders([t]));
                foreach (var tile in tiles)
                {
                    // Filled as the lines are computed, so a pass that throws later still has them.
                    var paths = pathsOf[tile] = [];
                    try
                    {
                        await RefreshGitAsync(tile, paths, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Git refresh of {Root} failed", tile.RootPath);
                    }
                }

                if (_gitHub is not null)
                {
                    // Only the tiles still there count: one removed during the pass must not be asked about, nor kept. The UI thread is not asked again:
                    // it may be busy, and this must not wait for it.
                    var current = tiles.Where(t => !t.Removed).ToList();
                    var paths = current.SelectMany(t => pathsOf[t]).Distinct().ToList();

                    // What no tile shows any more is let go, so a folder that comes back is read afresh. The set is what the pass computed and what the lines
                    // showed when it began: a folder that stopped resolving for a moment keeps its result for one pass, and a truly gone one goes with the next.
                    // A GitHub round still in flight can write a removed folder back afterwards; the next pass, 30 s at most, lets go of it again.
                    _gitHub.Retain([.. paths, .. current.SelectMany(t => shownBefore[t])]);

                    // A folder no GitHub round has looked at yet gets its pills now, not at the next ten minutes. Not awaited: a slow gh must not hold up git.
                    // A folder already asked for waits for that round to end, so a slow one is not queued behind itself every 30 s.
                    var round = new object();
                    var unknown = AskGitHubOf(paths.Where(p => !_gitHub.Knows(p)), round);
                    if (unknown.Count > 0)
                    {
                        _ = RefreshGitHubAsync(unknown, round);
                    }
                }
            }
            while (AnotherRefreshWasAskedFor());
        }
        catch (Exception ex)
        {
            lock (_gitGate)
            {
                _gitRefreshRunning = false;
                _gitRefreshAgain = false;
            }

            if (ex is OperationCanceledException)
            {
                throw;
            }

            _logger.LogWarning(ex, "The git refresh round ended early");
        }
    }

    /// <param name="paths">Gets the folder of each line computed, before the lines are posted: the tile shows them later.</param>
    /// <param name="worktrees">Also bring the tile's worktrees up to date, as a round does.</param>
    /// <returns>The lines the read found, as the tile shows them but for the GitHub pills.</returns>
    private async Task<IReadOnlyList<GitLine>> RefreshGitAsync(WorkspaceTileViewModel tile, List<string> paths, CancellationToken ct, bool worktrees = true)
    {
        // Taken before git is asked: of two reads of one tile, the one started later knows more, whichever ends first (#425).
        var read = tile.StartGitRead();
        var info = await _git.InspectAsync(tile.RootPath, ct).ConfigureAwait(false);
        // Read once for both steps: the second tells the lines the first carried over by their instances.
        var previous = tile.GitLines;
        var found = await GitLinesAsync(tile, info, previous, ct).ConfigureAwait(false);
        // GitHub's commit for each line as git is about to be asked about it: none where GitHub was not read yet.
        var given = found.Select(GitHubHeadOf).ToList();
        var lines = await WithBehindAsync(found, previous, ct).ConfigureAwait(false);
        paths.AddRange(lines.Where(HasRepository).Select(l => l.Path).OfType<string>());
        // A line carried over is the one shown before, its folder not looked at: asking git again for it would carry it over again.
        var compared = lines.Select(l => !previous.Any(p => ReferenceEquals(p, l))).ToList();
        // The pills are taken from the GitHub side when the tile shows the lines, not before: a result stored meanwhile is not overwritten by an older one.
        _ui.Post(() =>
        {
            var shown = lines.Select(WithGitHub).ToList();
            if (tile.ShowGit(shown, read))
            {
                FollowPanelBranch();

                // GitHub's commit moved while git compared against the one before, or GitHub was read only then (#429): the read of GitHub
                // saw no line to ask about, as the line was not on the branch yet. Asked again now, not at the next round, up to 30 s on.
                // Only for a line git compared this round, and against another commit than GitHub has now: a line git gave no answer for
                // was given the commit it has now, so this never asks round after round.
                if (shown.Select((l, i) => compared[i] && GitLine.ComparedHead(l, l.GitHub)?.Sha != given[i]).Any(again => again) && !_disposed)
                {
                    _ = RequestGitRefresh(CancellationToken.None, again: true);
                }
            }
        });

        // A worktree added next to the repository after the registration is a child root from here on, so the chat started
        // in it lands on this tile within one refresh; a removed one stops being one. Git that fails to answer changes nothing.
        // The process runs only where git records a linked worktree, or one is registered and may have been removed.
        if (!worktrees || !info.IsRepository || (tile.Workspace.Worktrees.Count == 0 && !_git.HasLinkedWorktrees(tile.RootPath)))
        {
            return lines;
        }

        var porcelain = await _git.RunAsync(tile.RootPath, "worktree list --porcelain", ct).ConfigureAwait(false);
        if (porcelain is not null)
        {
            await _registry.UpdateWorktreesAsync(tile.Workspace, WorkspaceProbe.ParseWorktreeList(porcelain, tile.RootPath), ct).ConfigureAwait(false);
        }

        return lines;
    }

    /// <summary>The line with the pills of the issues and pull requests last read for its repository (a count of zero has none), and what was read, which opens the panel (#406).</summary>
    private GitLine WithGitHub(GitLine line)
    {
        var activity = HasRepository(line) && line.Path is { } path ? _gitHub?.ForFolder(path) : null;
        return line with
        {
            Issues = activity is { OpenIssues: > 0 } ? new GitHubPill(activity, GitHubItemKind.Issue) : null,
            PullRequests = activity is { OpenPullRequests: > 0 } ? new GitHubPill(activity, GitHubItemKind.PullRequest) : null,
            GitHub = activity,
        };
    }

    /// <summary>
    /// Each line with how far it is behind GitHub (#426): a line on its repository's default branch is compared with the commit GitHub was last
    /// read to have there, by git and without a fetch, and only when one of the two commits changed since the folder's line in
    /// <paramref name="previous"/> was compared. A line carried over from <paramref name="previous"/> is left as it is: its folder could
    /// not be looked at this round (an offline share blocks every look for about 20 s).
    /// </summary>
    private async Task<IReadOnlyList<GitLine>> WithBehindAsync(IReadOnlyList<GitLine> lines, IReadOnlyList<GitLine> previous, CancellationToken ct)
    {
        if (_gitHub is null)
        {
            return lines;
        }

        return await Task.WhenAll(lines.Select(async line =>
        {
            if (previous.Any(p => ReferenceEquals(p, line)))
            {
                return line;
            }

            if (line.Path is not { } path || GitLine.ComparedHead(line, _gitHub.ForFolder(path)) is not { } onGitHub)
            {
                return line is { Behind: null, BehindAgainst: null } ? line : line with { Behind = null, BehindAgainst = null };
            }

            var key = YardGitJobs.Key(path);
            var before = previous.FirstOrDefault(p => p.Path is { } shown && YardGitJobs.Key(shown) == key);
            if (line.IsAnsweredBy(before, onGitHub.Sha))
            {
                return line with { Behind = before.Behind, BehindAgainst = onGitHub.Sha };
            }

            return line.WithBehind(await _git.BehindAsync(path, line.Head, onGitHub.Sha, onGitHub.Commits, ct).ConfigureAwait(false), before, onGitHub.Sha);
        })).ConfigureAwait(false);
    }

    /// <summary>The commit GitHub was last read to have for <paramref name="line"/> to be compared with (#426); null when there is none.</summary>
    private string? GitHubHeadOf(GitLine line) => line.Path is { } path ? GitLine.ComparedHead(line, _gitHub?.ForFolder(path))?.Sha : null;

    /// <summary>The line's folder is in a repository of its own, whatever its branch: a git above the user profile is not this folder's repository, and its origin is not the folder's.</summary>
    private static bool HasRepository(GitLine line) => line.IsRepository;

    /// <summary>
    /// Called on the UI thread, which owns the tiles. Only a round the button asked for starts the failure line; once it shows, every round
    /// brings it up to date, so it says what the last round found and goes when nothing is left unread.
    /// </summary>
    private async Task RefreshGitHubOfAllTilesAsync(bool pressed)
    {
        var round = await RefreshGitHubAsync(Tiles.SelectMany(t => t.GitLines).Where(HasRepository).Select(l => l.Path).OfType<string>().Distinct().ToList()).ConfigureAwait(false);
        _ui.Post(() =>
        {
            if (pressed || RefreshMessage is not null)
            {
                RefreshMessage = FailureLine(round);
            }
        });
    }

    /// <summary>A click on the failure line puts it away; only another press can bring it back.</summary>
    [RelayCommand]
    private void DismissRefreshMessage()
    {
        RefreshMessage = null;
        JobMessage = null;
        _jobMessageCheckout = null;
    }

    /// <summary>One repository that cannot be read is no failure of GitHub, and "any pills": a gh that never answered has read none.</summary>
    private static string? FailureLine(GitHubRoundResult round)
    {
        if (round.Unread == 0)
        {
            return null;
        }

        return round.Unread == round.Repositories
            ? "Couldn't read GitHub. Check gh in Settings, GitHub. Any pills show what was last read."
            : $"Couldn't read {round.Unread} of {round.Repositories} repositories. Any pills show what was last read.";
    }

    /// <summary>Off the caller's thread from the start: up to its first await a round starts one git per folder, and starting one for a folder on an offline share blocks for about 20 s.</summary>
    /// <returns>What the round came to; nothing unread when the Yard closed first, since there is then nothing to say, and everything unread when the round ended early.</returns>
    private Task<GitHubRoundResult> RefreshGitHubAsync(IReadOnlyCollection<string> folders, object? asked = null) => Task.Run(async () =>
    {
        try
        {
            return await _gitHub!.RefreshAsync(folders, _gitHubStop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The Yard is closing.
            return default;
        }
        catch (Exception ex)
        {
            // The type and the stack, never the message: it could carry a title, a label or a repository name.
            _logger.LogWarning("The GitHub refresh round ended early: {Failure}", FailureText.Of(ex));
            return new GitHubRoundResult(1, 1);
        }
        finally
        {
            if (asked is not null)
            {
                lock (_gitHubAsked)
                {
                    // Only its own asks: a folder asked for again meanwhile (removed and added back) belongs to the later round.
                    foreach (var folder in folders.Where(f => _gitHubAsked.TryGetValue(f, out var by) && by == asked))
                    {
                        _gitHubAsked.Remove(folder);
                    }
                }
            }
        }
    });

    /// <summary>True the first time a folder is asked for; again only after the round that was asked to read it has ended, whatever it came to.</summary>
    private List<string> AskGitHubOf(IEnumerable<string> folders, object round)
    {
        lock (_gitHubAsked)
        {
            return folders.Where(f => _gitHubAsked.TryAdd(f, round)).ToList();
        }
    }

    private void OnGitHubChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        var moved = false;
        foreach (var tile in Tiles)
        {
            var lines = tile.GitLines.Select(WithGitHub).ToList();
            moved |= lines.Any(l => l.BehindIsOutOfDate);
            tile.ShowGit(lines);
        }

        // Where a default branch stands on GitHub now decides which lines are behind (#426), and only git can say: asked at once, not at
        // the next round, up to 30 s on. A merge from the Yard reads GitHub again, so its line says "behind" as soon as that read is in.
        if (moved && !_disposed)
        {
            _ = RequestGitRefresh(CancellationToken.None, again: true);
        }
    });

    /// <summary>The tile the open panel follows its checkout on (<see cref="FollowPanelBranch"/>) reads its git now (#429).</summary>
    private void ReadGitOfCheckout(string path)
    {
        var key = YardGitJobs.Key(path);
        if (Tiles.FirstOrDefault(t => t.GitLines.Any(l => l.Path is { } p && YardGitJobs.Key(p) == key)) is { } tile)
        {
            RefreshGitOf(tile);
        }
    }

    /// <summary>The open panel's Pull names what a press does now: its line's branch as last read (review of #407).</summary>
    private void FollowPanelBranch()
    {
        if (GitHubPanel?.CheckoutPath is { } path && LinesOf(path).FirstOrDefault() is { } line)
        {
            GitHubPanel.FollowBranch(line.Branch, line.TrackedChanges);
        }
    }

    /// <summary>Every tile's line of the checkout: a folder can be on the tiles of several workspaces.</summary>
    private IEnumerable<GitLine> LinesOf(string path)
    {
        var key = YardGitJobs.Key(path);
        return Tiles.SelectMany(t => t.GitLines).Where(l => l.Path is { } p && YardGitJobs.Key(p) == key);
    }

    /// <summary>
    /// A chat working in the checkout, or waiting mid-turn for an answer (a permission, a question), on any tile with a line of the same
    /// checkout (its git directory: a workspace on a subfolder of it, #412), of the folder, or of the same GitHub repository: the Yard never
    /// switches a branch under one (#407). By tile, not by chat folder, and another clone of the repository counts too: it errs on the careful side.
    /// </summary>
    private bool ChatIsWorkingIn(GitLine line, GitHubRepo repo) =>
        Tiles.Any(t => (t.IsWorking || t.NeedsAttention) && t.GitLines.Any(l => SameCheckout(l, line) || l.GitHub?.Repo == repo));

    /// <summary>
    /// Two lines are the same checkout (#412): the same folder, or the same repository's git directory when both know it (a workspace on a
    /// subfolder, a linked worktree). The same folder counts whatever git says of it now: a folder cloned again is still where the chat works.
    /// </summary>
    internal static bool SameCheckout(GitLine a, GitLine b) =>
        (a.Path is { } p && b.Path is { } q && YardGitJobs.Key(p) == YardGitJobs.Key(q))
        || (YardGitJobs.CommonGitDir(a.GitDir) is { } x && YardGitJobs.CommonGitDir(b.GitDir) is { } y && YardGitJobs.Key(x) == YardGitJobs.Key(y));

    /// <summary>
    /// A line for the root folder, and one more for each other repository the tile's workspace file lists a folder of
    /// (Diffusion-Full: DiffusionNexus.Installer.SDK and DiffusionNexus), each under the name the file gives the folder, or
    /// its own. A folder in a checkout already shown adds none. A folder that is in no repository shows the repositories
    /// one level below it instead, each under its own folder's name (a folder of package repositories); the root's
    /// "no git" line gives way to them, and a folder with none below it adds nothing, or keeps the root's "no git". A
    /// folder the file lists a repository within is left as it is: the file picks and names the repositories there. Never
    /// throws but for cancellation, so the other folders cannot hold back the root's line or the worktree sync: a folder
    /// whose check fails, and a file that cannot be read for a moment, keep last round's lines. Runs off the UI thread: it
    /// reads the file.
    /// </summary>
    /// <param name="previous">The tile's lines as last shown: what a folder that cannot be checked this round keeps.</param>
    private async Task<IReadOnlyList<GitLine>> GitLinesAsync(WorkspaceTileViewModel tile, GitInfo root, IReadOnlyList<GitLine> previous, CancellationToken ct)
    {
        // The stable-build scripts (#423) are read where a line is made afresh: a line carried over keeps what it had, as its
        // folder could not be checked (an offline share blocks every look for about 20 s). The root just answered git.
        var rootStableBuild = StableBuildOf(root, previous);
        var folders = tile.Workspace.WorkspaceFile is { } file ? WorkspaceProbe.FoldersOf(file) : [];
        if (folders is null)
        {
            // The root's own line is brought up to date where it leads; where repositories below the root took its place, they stay as they were.
            return previous[0].Path is { } first && PathNormalizer.Normalize(first) != PathNormalizer.Normalize(tile.RootPath)
                ? previous
                : [previous[0] with
                {
                    Branch = root.Branch, DirtyCount = root.DirtyCount, Path = tile.RootPath, IsRepository = root.IsRepository, GitDir = root.GitDir,
                    StableBuild = rootStableBuild, Head = root.Head, TrackedChanges = root.TrackedChanges,
                }, .. previous.Skip(1)];
        }

        var rootKey = PathNormalizer.Normalize(tile.RootPath);
        var rootLine = new GitLine(
            folders.FirstOrDefault(f => PathNormalizer.Normalize(f.Path) == rootKey)?.Label ?? WorkspaceProbe.FolderName(tile.RootPath),
            root.Branch,
            root.DirtyCount,
            tile.RootPath,
            IsRepository: root.IsRepository,
            GitDir: root.GitDir,
            StableBuild: rootStableBuild,
            Head: root.Head,
            TrackedChanges: root.TrackedChanges);
        var others = folders.Where(f => PathNormalizer.Normalize(f.Path) != rootKey).ToList();

        // Each folder's git directory first, which reads a file or two, so git status runs once per checkout, not once per
        // folder in it. Both steps look at the folders side by side: one on an offline share blocks Directory.Exists for
        // about 20 s, and that should cost the round once, not once per folder.
        // The root's listing runs beside the folders' checks, and is set aside when they find the file picks the repositories below it.
        var rootBelowTask = root.GitDir is null ? RepositoriesBelowAsync(tile.RootPath, ct) : Task.FromResult(ReposBelow.None);
        var gitDirs = await Task.WhenAll(others.Select(f => GitDirOfAsync(f, ct))).ConfigureAwait(false);
        var listedRepositories = others.Zip(gitDirs).Where(p => p.Second.GitDir is not null).Select(p => PathNormalizer.Normalize(p.First.Path)).ToList();
        var belowOthers = await Task.WhenAll(others.Zip(gitDirs).Select(p => p.Second is { GitDir: null, Failed: false } && ListsNoRepositoryWithin(p.First.Path)
            ? RepositoriesBelowAsync(p.First.Path, ct)
            : Task.FromResult(ReposBelow.None))).ConfigureAwait(false);
        var rootBelow = ListsNoRepositoryWithin(tile.RootPath) ? await rootBelowTask.ConfigureAwait(false) : ReposBelow.None;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.GitDir is { } rootGitDir)
        {
            seen.Add(rootGitDir);
        }

        // In the order they show: the repositories below the root, then each listed folder's line or the repositories below it.
        var pending = new List<Task<GitLine?>>();
        AddBelow(tile.RootPath, rootBelow);
        var belowRoot = pending.Count;
        foreach (var (folder, (gitDir, failed), below) in others.Zip(gitDirs, belowOthers))
        {
            if (failed)
            {
                pending.Add(Task.FromResult(PreviousLine(folder.Path)));
            }
            else if (gitDir is null)
            {
                AddBelow(folder.Path, below);
            }
            else if (seen.Add(gitDir))
            {
                pending.Add(LineAsync(folder));
            }
        }

        var lines = await Task.WhenAll(pending).ConfigureAwait(false);
        var replacesRoot = lines.Take(belowRoot).Any(l => l is not null);
        List<GitLine> all = replacesRoot ? [.. lines.OfType<GitLine>()] : [rootLine, .. lines.OfType<GitLine>()];
        // Only the root's own line leaves its name out: a lone repository below the root is not the workspace's folder.
        return all.Count > 1 || replacesRoot ? all : [rootLine with { Folder = null }];

        // A listed folder within it that is in a repository: the file picks and names the repositories there itself.
        bool ListsNoRepositoryWithin(string folder)
        {
            var key = PathNormalizer.Normalize(folder);
            return !listedRepositories.Any(k => k != key && PathNormalizer.IsWithin(k, key));
        }

        void AddBelow(string parent, ReposBelow below)
        {
            if (below.Failed)
            {
                pending.AddRange(previous.Where(l => l.IsRepository && l.Path is { } path && IsChildOf(path, parent)).Select(l => Task.FromResult<GitLine?>(l)));
                return;
            }

            pending.AddRange(below.Repositories.Where(r => seen.Add(r.GitDir)).Select(r => LineAsync(r.Folder)));
        }

        GitLine? PreviousLine(string folder) =>
            previous.FirstOrDefault(l => l.Path is { } path && PathNormalizer.Normalize(path) == PathNormalizer.Normalize(folder));

        async Task<GitLine?> LineAsync(WorkspaceFolder folder)
        {
            try
            {
                var info = await _git.InspectAsync(folder.Path, ct).ConfigureAwait(false);
                return new GitLine(folder.Label, info.Branch, info.DirtyCount, folder.Path, IsRepository: info.IsRepository, GitDir: info.GitDir,
                    StableBuild: StableBuildOf(info, previous), Head: info.Head, TrackedChanges: info.TrackedChanges);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Git refresh of {Folder}, a folder of {Root}, failed", folder.Path, tile.RootPath);
                return PreviousLine(folder.Path);
            }
        }
    }

    /// <summary>
    /// The stable-build scripts at the top of the checkout git just looked at (#423), where git found it (#425): one walk up, the one that
    /// knows worktree pointers and stops short of the user profile. The tile's lines as last shown hold what the last round found there.
    /// </summary>
    private static StableBuildRepo? StableBuildOf(GitInfo info, IReadOnlyList<GitLine> previous)
    {
        if (!info.IsRepository || info.Top is not { } top)
        {
            return null;
        }

        return StableBuildRepo.Find(top, previous.Select(l => l.StableBuild));
    }

    private async Task<(string? GitDir, bool Failed)> GitDirOfAsync(WorkspaceFolder folder, CancellationToken ct)
    {
        try
        {
            return (await Task.Run(() => _git.GitDirOf(folder.Path), ct).ConfigureAwait(false), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Finding the repository of {Folder} failed", folder.Path);
            return (null, true);
        }
    }

    /// <summary>A repository one level below a folder that is in none, with its git directory.</summary>
    private sealed record RepoBelow(WorkspaceFolder Folder, string GitDir);

    /// <param name="Failed">The folder could not be listed right now.</param>
    private sealed record ReposBelow(IReadOnlyList<RepoBelow> Repositories, bool Failed)
    {
        public static readonly ReposBelow None = new([], false);
    }

    /// <summary>
    /// The repositories one level below a folder that is in none, by folder name, each with its git directory: a child
    /// with a <c>.git</c> of its own. Deeper folders are not looked at, so the round costs one folder listing. Failed when
    /// the folder cannot be listed right now; a folder that is gone has none.
    /// </summary>
    private async Task<ReposBelow> RepositoriesBelowAsync(string parent, CancellationToken ct)
    {
        try
        {
            return new ReposBelow(await Task.Run(() => RepositoriesBelow(parent), ct).ConfigureAwait(false), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Finding the repositories below {Folder} failed", parent);
            return new ReposBelow([], true);
        }
    }

    private List<RepoBelow> RepositoriesBelow(string parent)
    {
        if (!Directory.Exists(parent))
        {
            return [];
        }

        var found = new List<RepoBelow>();
        foreach (var child in Directory.EnumerateDirectories(parent).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Path.Exists(Path.Combine(child, ".git")) && _git.GitDirOf(child) is { } gitDir)
                {
                    found.Add(new RepoBelow(new WorkspaceFolder(child, null), gitDir));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A .git pointer that cannot be read is that folder's; it holds back none of the repositories beside it.
                _logger.LogWarning(ex, "Finding the repository of {Folder} failed", child);
            }
        }

        return found;
    }

    private static bool IsChildOf(string path, string parent) =>
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) is { } directory
        && PathNormalizer.Normalize(directory) == PathNormalizer.Normalize(parent);

    /// <summary>True to run once more; otherwise the round is over, decided in the same critical section, so no request is lost.</summary>
    private bool AnotherRefreshWasAskedFor()
    {
        lock (_gitGate)
        {
            if (_gitRefreshAgain)
            {
                _gitRefreshAgain = false;
                return true;
            }

            _gitRefreshRunning = false;
            return false;
        }
    }

    /// <summary>The tiles as of now, read on the UI thread, which alone enumerates the tile collections; waits for it at most <see cref="UiTimeout"/>.</summary>
    private Task<List<WorkspaceTileViewModel>> TilesOnUiThreadAsync() => _ui.InvokeAsync(() => Tiles.ToList(), UiTimeout);

    [RelayCommand]
    private void AddWorkspace() => AddWorkspaceRequested?.Invoke(null);

    public void AddWorkspaceFrom(string path) => AddWorkspaceRequested?.Invoke(path);

    /// <summary>
    /// The path to add when <paramref name="paths"/> are dropped on the Yard, or null to refuse the drop: exactly one
    /// path, shaped like one the Add workspace dialog can detect (judged without the disk: it runs in DragEnter). The
    /// dialog adds one workspace at a time, so several are refused rather than all but one dropped silently.
    /// </summary>
    public static string? DroppedWorkspacePath(IReadOnlyList<string>? paths) => paths is [var path] && WorkspaceProbe.LooksProbeable(path) ? path : null;

    /// <summary>The panel a repository line or a pill opens with the repository's open pull requests and issues; null when closed. Another opening replaces it.</summary>
    [ObservableProperty] private GitHubPanelViewModel? _gitHubPanel;

    /// <summary>The pulls the panel starts (#407): they outlive the panel.</summary>
    public YardGitJobs GitJobs { get; }

    /// <summary>A click on a pill opens the repository's panel over the Yard, on that pill's tab: it is still a click on its line (#406). Without the readers (tests, no gh) it does nothing.</summary>
    [RelayCommand]
    private void OpenGitHubList(GitHubPill pill)
    {
        var line = Tiles.SelectMany(t => t.GitLines).FirstOrDefault(l => ReferenceEquals(l.Issues, pill) || ReferenceEquals(l.PullRequests, pill));
        OpenPanel(pill.Activity, pill.Kind, line);
    }

    /// <summary>
    /// A click on a tile's repository line opens its panel (#406), on pull requests when any are open; a line with nothing open opens
    /// it too. Always enabled: the line's pills sit inside its button, and a button turned off would turn them off with it. The line
    /// is a button only when it is on GitHub, and without the readers there is nothing on GitHub.
    /// </summary>
    [RelayCommand]
    private void OpenGitHubLine(GitLine? line)
    {
        if (line?.GitHub is { } activity)
        {
            OpenPanel(activity, GitHubPanelViewModel.DefaultKind(activity), line);
        }
    }

    /// <param name="line">The line the panel was opened from: where its Pull acts (#407). Null when it is gone; then the panel has no Pull.</param>
    private void OpenPanel(RepoActivity activity, GitHubItemKind kind, GitLine? line)
    {
        if (_gitHub is null || _gitHubReader is null || _gitHubBrowsers is null)
        {
            return;
        }

        GitHubPanel?.CloseCommand.Execute(null);
        var checkout = line?.Path is { } path
            ? new PanelCheckout(path, line.Branch, () => ChatIsWorkingIn(line, activity.Repo), GitJobs, line.GitDir, line.TrackedChanges, () => ReadGitOfCheckout(path))
            : null;
        var panel = new GitHubPanelViewModel(activity, kind, _gitHubReader, _gitHub, _gitHubBrowsers, _clipboard, _ui, _time, _logger,
            closed => { if (ReferenceEquals(GitHubPanel, closed)) { GitHubPanel = null; } }, checkout);
        GitHubPanel = panel;
        // Not on the UI thread: the load starts its first gh before its first await.
        _ = Task.Run(() => panel.LoadAsync(_gitHubStop.Token));
    }

    /// <summary>
    /// A pull ended (#407): the lines are read again, so the branch shows what git did. A failure no panel showed goes on the failure line,
    /// so a panel closed while it ran does not hide it.
    /// </summary>
    private void OnGitJobEnded(GitJobOutcome outcome, bool taken)
    {
        if (_disposed)
        {
            return;
        }

        // A later pull of the same checkout says the last word on it, whatever it came to.
        var key = outcome.Key;
        if (_jobMessageCheckout == key)
        {
            JobMessage = null;
            _jobMessageCheckout = null;
        }

        if (!taken && !outcome.Succeeded)
        {
            JobMessage = $"{outcome.Name}: {outcome.Message}";
            _jobMessageCheckout = key;
        }

        _ = RefreshGitAfterJobAsync();

        // A merge changes the repository's open pull requests: its pills and counts are read now, not at the next ten minutes (#408), also
        // after one gh ran out of time on, which may have gone through.
        if (outcome.ChangedGitHub && _gitHub is not null)
        {
            _ = RefreshGitHubAsync([outcome.Checkout]);
        }
    }

    private async Task RefreshGitAfterJobAsync()
    {
        try
        {
            await RefreshGitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The git refresh after a pull failed");
        }
    }

    /// <summary>A refresh the button asked for is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshButtonText))]
    private bool _isRefreshing;

    public string RefreshButtonText => IsRefreshing ? "Refreshing…" : "Refresh";

    /// <summary>Says what could not be read when the button asked. A round that reads everything takes it away, the button's or the timer's, and so does a click on it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FailureLineText))]
    private string? _refreshMessage;

    /// <summary>
    /// A pull's failure no panel showed (#407), named after its repository: kept apart from <see cref="RefreshMessage"/>, so the rounds
    /// neither take it away nor start saying what they found because of it (review of #407). A later pull of that checkout takes it away.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FailureLineText))]
    private string? _jobMessage;

    /// <summary>The checkout <see cref="JobMessage"/> is about, by <see cref="YardGitJobs.Key"/>. UI thread.</summary>
    private string? _jobMessageCheckout;

    /// <summary>What the failure line on the top bar says: a pull's failure, then the round's, one to a line; null hides it.</summary>
    public string? FailureLineText => JobMessage is null ? RefreshMessage : RefreshMessage is null ? JobMessage : $"{JobMessage}\n{RefreshMessage}";

    /// <summary>
    /// The button on the Yard's top bar (#382): the git info of every tile, then the GitHub pills of the lines that gives, without waiting
    /// for the timers, which keep running. A second press while this runs starts nothing. The command stays enabled for it: WPF would drop
    /// the keyboard to the window from a button it disables while focused, and it would not come back.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Refresh()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            await RefreshGitAsync(CancellationToken.None);
            if (_gitHub is not null)
            {
                await RefreshGitHubOfAllTilesAsync(pressed: true);
            }
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    partial void OnNeedsMeFirstChanged(bool value) => Resort();

    /// <summary>
    /// A chat is shown on the tile of the workspace the engine maps it to, and on no other: a row left on a tile the
    /// engine has moved it away from (another workspace, or none when its cwd left every root) would never change again.
    /// </summary>
    internal void Apply(SessionSnapshot snapshot)
    {
        if (IsClosed(snapshot))
        {
            return;
        }

        // On its tile first: the tile it leaves then knows the chat is another's, and keeps no row for a tab of it.
        var tile = snapshot.WorkspaceId is { } workspaceId ? FindTile(workspaceId) : null;
        tile?.Upsert(snapshot, _pricing.Pricing);
        foreach (var other in Tiles.Where(t => t != tile && t.Knows(snapshot.SessionId)).ToList())
        {
            other.Remove(snapshot.SessionId);
        }

        if (tile?.Chats.FirstOrDefault(c => c.SessionId == snapshot.SessionId) is { } row)
        {
            row.VoiceLabel = _voiceLabels.GetValueOrDefault(snapshot.SessionId);
        }

        Resort();
    }

    internal void Apply(HostStateChanged change)
    {
        var tile = FindTile(change.WorkspaceId);
        if (tile is null)
        {
            return;
        }

        var wasRunning = tile.HostState == HostState.Running;
        tile.HostState = change.State;
        Resort(); // the rows it shows changed with it
        if (wasRunning && change.State == HostState.Stopped)
        {
            HostStopped?.Invoke(change.WorkspaceId);
        }
    }

    private void AddTile(Workspace workspace)
    {
        if (FindTile(workspace.Id) is not null)
        {
            return;
        }

        // Named "Track" until the read lands: the tiles that come in on the same track meanwhile ask for no read of their own.
        if (_trackNames.TryAdd(workspace.TrackId, TrackNameOf(workspace.TrackId)))
        {
            _ = ReloadTrackNamesAsync();
        }

        var tile = new WorkspaceTileViewModel(workspace, this);
        foreach (var snapshot in _engine.Snapshots.Where(s => s.WorkspaceId == workspace.Id && !IsClosed(s)))
        {
            tile.Upsert(snapshot, _pricing.Pricing);
        }

        // Straight into its group: the pointer may hold the order, and a tile should not land in Older to move later.
        GroupOf(YardGroups.Of(tile.LastUsedAt, tile.IsWorking, tile.NeedsAttention, NeedsMeFirst, Now)).Tiles.Add(tile);
        Resort();
        TilesChanged?.Invoke();
        _ = RefreshGitAsync(CancellationToken.None);
        CurrentTabsRefresh = RefreshTabsAsync(); // a read that runs now was not asked about the tile: another follows it
    }

    /// <summary>
    /// Reads the tabs again now, or after the read that runs (UI thread): a tile's VS Code stopped running. When it
    /// <paramref name="closed"/>, once more <see cref="ClosedListWait"/> later: it writes the tabs it comes back with as its
    /// window goes.
    /// </summary>
    internal void RefreshTabsSoon(bool closed = false)
    {
        CurrentTabsRefresh = RefreshTabsAsync();
        if (closed && !_disposed) // not for a state posted before the app closed
        {
            // One for each close: a window closed soon after another does not put off the first one's look.
            ITimer? timer = null;
            timer = _time.CreateTimer(_ => _ui.Post(() =>
            {
                if (_closedListTimers.Remove(timer!))
                {
                    timer!.Dispose();
                    CurrentTabsRefresh = RefreshTabsAsync();
                }
            }), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _closedListTimers.Add(timer);
            timer.Change(ClosedListWait, Timeout.InfiniteTimeSpan); // armed once it is in the set
        }
    }

    /// <summary>Reads the names of the tracks again: a tile came in on a track not known yet.</summary>
    private async Task ReloadTrackNamesAsync()
    {
        try
        {
            var tracks = await _store.GetTracksAsync(CancellationToken.None);
            _ui.Post(() =>
            {
                foreach (var track in tracks)
                {
                    _trackNames[track.Id] = track.Name;
                }
            });
        }
        catch (Exception ex)
        {
            // Fire-and-forget from AddTile: a failed read would otherwise leave no trace, and the track its placeholder name.
            _logger.LogError(ex, "Reading the tracks for a new tile's track failed; the track keeps its placeholder name");
        }
    }

    private static List<string> ShownFolders(IEnumerable<WorkspaceTileViewModel> tiles) =>
        tiles.SelectMany(t => t.GitLines).Select(l => l.Path).OfType<string>().ToList();

    private void RemoveTile(Guid workspaceId)
    {
        foreach (var group in Groups)
        {
            var tile = group.Tiles.FirstOrDefault(t => t.Id == workspaceId);
            if (tile is not null)
            {
                group.Tiles.Remove(tile);
                tile.Removed = true;
                // Not left to the next git pass: the workspace added again within 30 s must not show the old counts, nor wait for a round asked for before.
                if (_gitHub is not null)
                {
                    var kept = ShownFolders(Tiles);
                    _gitHub.Retain(kept);
                    lock (_gitHubAsked)
                    {
                        foreach (var folder in ShownFolders([tile]).Except(kept, StringComparer.OrdinalIgnoreCase))
                        {
                            _gitHubAsked.Remove(folder);
                        }
                    }
                }
                TilesChanged?.Invoke();
                TileRemoved?.Invoke(workspaceId);
                return;
            }
        }
    }

    /// <summary>
    /// Puts each tile in its group (<see cref="YardGroups.Of"/>), unless the pointer holds the order (<see cref="HoldOrder"/>),
    /// and orders each group: under Needs me first a tile waiting on
    /// the user leads, then one that works; then the newest last use first. Working tiles count as used now, so two of them
    /// keep their places by name rather than swap with each event; one that stops goes by when it stopped.
    /// </summary>
    private void Resort()
    {
        if (OrderHeld)
        {
            return;
        }

        var now = Now;

        var changed = false;
        foreach (var group in Groups)
        {
            foreach (var tile in group.Tiles.ToList())
            {
                var belongs = YardGroups.Of(tile.LastUsedAt, tile.IsWorking, tile.NeedsAttention, NeedsMeFirst, now);
                if (belongs != group.Group)
                {
                    group.Tiles.Remove(tile);
                    GroupOf(belongs).Tiles.Add(tile);
                    changed = true;
                }
            }
        }

        foreach (var group in Groups)
        {
            var ordered = group.Tiles.OrderBy(t => NeedsMeFirst ? t.AttentionRank : 0)
                .ThenByDescending(t => t.IsWorking ? DateTimeOffset.MaxValue : t.LastUsedAt ?? DateTimeOffset.MinValue)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var current = group.Tiles.IndexOf(ordered[i]);
                if (current != i)
                {
                    group.Tiles.Move(current, i);
                    changed = true;
                }
            }
        }

        if (changed)
        {
            OnPropertyChanged(nameof(Tiles));
        }
    }

    public void Dispose()
    {
        _tickTimer?.Dispose();
        _gitTimer?.Dispose();
        _gitHubTimer?.Dispose();
        if (_gitHub is not null)
        {
            _gitHub.Changed -= OnGitHubChanged;
        }

        GitJobs.Ended -= OnGitJobEnded;

        // Not disposed: the source has no timer or registration that needs it, and Dispose may run twice.
        _gitHubStop.Cancel();
        _tabsTimer?.Dispose();
        _disposed = true;
        foreach (var timer in _closedListTimers)
        {
            timer.Dispose();
        }

        _closedListTimers.Clear();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        // Last: it runs the UI's close logic, and a subscriber that throws there must not skip the cleanup above.
        GitHubPanel?.CloseCommand.Execute(null);
    }
}
