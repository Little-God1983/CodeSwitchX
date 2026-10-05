using System.Collections.ObjectModel;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Paths;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Yard;

/// <summary>The tile board: Track groups of workspace tiles, each with live chat rows.</summary>
public sealed partial class YardViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan GitRefreshInterval = TimeSpan.FromSeconds(30);

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
    private readonly Func<string, DateTimeOffset?> _writtenInAt;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Lock _gitGate = new();
    private ITimer? _tickTimer;
    private ITimer? _gitTimer;
    private ITimer? _tabsTimer;

    /// <summary>Whether a read of the tabs runs: the next one waits for it (UI thread).</summary>
    private bool _tabsRefreshing;

    /// <summary>When the chats of tabs the app knows no chat of were last written in; looked up once a chat (UI thread).</summary>
    private readonly Dictionary<string, DateTimeOffset> _tabActivity = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tabActivityAsked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a closed chat can be kept on its tile, in minutes; 0 is not at all.</summary>
    public static readonly IReadOnlyList<int> KeepClosedMinutesChoices = [0, 1, 5, 10, 30];

    /// <summary>How long a chat can be idle before it is hidden, in hours; 0 is never.</summary>
    public static readonly IReadOnlyList<int> HideIdleHoursChoices = [0, 1, 4, 12, 24];

    /// <summary>How often VS Code's lists of open chat tabs are looked at; a list is read again only when its file changed.</summary>
    public static readonly TimeSpan TabsInterval = TimeSpan.FromSeconds(5);

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

    /// <summary>The time the tiles go by.</summary>
    internal DateTimeOffset Now => _time.GetUtcNow();

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
        set => SetProperty(ref _tileScale, double.IsFinite(value) ? Math.Round(Math.Clamp(value, MinTileScale, MaxTileScale), 2) : 1);
    }

    public YardViewModel(IWorkspaceStore store, WorkspaceRegistry registry, SessionEngine engine, IPricingProvider pricing, GitInspector git,
        IEventBus bus, IUiDispatcher ui, TimeProvider time, ILogger<YardViewModel> logger, IVsCodeOpenTabs? openTabs = null,
        Func<string, DateTimeOffset?>? writtenInAt = null)
    {
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

    public ObservableCollection<TrackGroupViewModel> Tracks { get; } = [];

    public IEnumerable<WorkspaceTileViewModel> Tiles => Tracks.SelectMany(t => t.Tiles);

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
        Tracks.Clear();
        // Ordinal, ignoring case, like AddTile and Resort, so a tile keeps its place whichever adds it.
        foreach (var track in tracks.OrderBy(t => t.SortOrder).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var group = new TrackGroupViewModel(track);
            foreach (var workspace in workspaces.Where(w => w.TrackId == track.Id).OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase))
            {
                group.Tiles.Add(new WorkspaceTileViewModel(workspace, this));
            }

            Tracks.Add(group);
        }

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
            _tabsTimer = _time.CreateTimer(_ => _ui.Post(() => _ = RefreshTabsAsync()), null, TabsInterval, TabsInterval);
        }

        _tickTimer = _time.CreateTimer(_ => _ui.Post(() => Tick(_time.GetUtcNow())), null, TickInterval, TickInterval);
        _ = RefreshGitAsync(CancellationToken.None);
        // The tick asks for nothing while a round runs: a round longer than the interval would otherwise repeat back to back.
        _gitTimer = _time.CreateTimer(_ => _ = RequestGitRefresh(CancellationToken.None, again: false), null, GitRefreshInterval, GitRefreshInterval);
    }

    public WorkspaceTileViewModel? FindTile(Guid workspaceId) => Tiles.FirstOrDefault(t => t.Id == workspaceId);

    /// <summary>How long a tile stays lit after <see cref="Spotlight"/>.</summary>
    public static readonly TimeSpan SpotlightTime = TimeSpan.FromSeconds(2);

    private ITimer? _spotlightTimer;

    /// <summary>Each spotlight's number: a timer of an earlier one that already fired puts out nothing (UI thread).</summary>
    private long _spotlights;

    /// <summary>Lights the workspace's tile for <see cref="SpotlightTime"/>, so the eye finds it; false when the board has no such tile (UI thread).</summary>
    public bool Spotlight(Guid workspaceId)
    {
        if (FindTile(workspaceId) is not { } tile)
        {
            return false;
        }

        _spotlightTimer?.Dispose();
        foreach (var lit in Tiles.Where(t => t.IsSpotlit))
        {
            lit.IsSpotlit = false;
        }

        tile.IsSpotlit = true;
        var number = ++_spotlights;
        _spotlightTimer = _time.CreateTimer(_ => _ui.Post(() =>
        {
            if (number == _spotlights)
            {
                tile.IsSpotlit = false;
            }
        }), null, SpotlightTime, Timeout.InfiniteTimeSpan);
        return true;
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
            tile.Forget(sessionId);
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
    /// A tab of a chat another tile shows is not shown twice. Called on the UI thread; a read still running is not doubled.
    /// </summary>
    internal async Task RefreshTabsAsync()
    {
        if (_openTabs is not { } openTabs || _tabsRefreshing)
        {
            return;
        }

        _tabsRefreshing = true;
        try
        {
            var workspaces = Tiles.Select(t => t.Workspace).ToList();
            var asked = _tabActivityAsked.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var (tabs, activity) = await Task.Run(() =>
            {
                var read = openTabs.Read(workspaces);
                // Only for tabs of chats the app has no state of: theirs is the last thing known of them.
                var written = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in read.Values.SelectMany(t => t.Tabs).Select(t => t.SessionId).Where(id => !asked.Contains(id) && _engine.Get(id) is null))
                {
                    written[id] = _writtenInAt(id);
                }

                return (read, written);
            });

            foreach (var (id, at) in activity)
            {
                _tabActivityAsked.Add(id);
                if (at is { } known)
                {
                    _tabActivity[id] = known;
                }
            }

            foreach (var tile in Tiles.ToList())
            {
                var ofTile = tabs.GetValueOrDefault(tile.Id);
                // Shown on the tile of the folder it runs in (a multi-root window), it is not shown here too; once it
                // shows there no more, its tab does here.
                var others = Tiles.Where(t => t != tile).ToList();
                tile.ShowTabs(ofTile is null ? null : ofTile with { Tabs = [.. ofTile.Tabs.Where(tab => !others.Exists(t => t.Shows(tab.SessionId)))] },
                    _tabActivity);
            }

            if (NeedsMeFirst)
            {
                Resort();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading VS Code's open chat tabs failed; the tiles keep the ones read before");
        }
        finally
        {
            _tabsRefreshing = false;
        }
    }

    public void RequestOpen(Guid workspaceId) => OpenRequested?.Invoke(workspaceId);

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
        if (NeedsMeFirst)
        {
            Resort();
        }
    }

    /// <summary>
    /// Refreshes every tile's git facts and the worktrees of every repository workspace. One round runs at a time; a
    /// refresh asked for while one runs (a workspace added meanwhile, the Refresh command) makes it run once more when it
    /// ends, and the task returned to either caller ends with that. A tile whose check fails is logged, and the others go on.
    /// </summary>
    public Task RefreshGitAsync(CancellationToken ct) => RequestGitRefresh(ct, again: true);

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
                foreach (var tile in await TilesOnUiThreadAsync().ConfigureAwait(false))
                {
                    try
                    {
                        await RefreshGitAsync(tile, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Git refresh of {Root} failed", tile.RootPath);
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

    private async Task RefreshGitAsync(WorkspaceTileViewModel tile, CancellationToken ct)
    {
        var info = await _git.InspectAsync(tile.RootPath, ct).ConfigureAwait(false);
        var lines = await GitLinesAsync(tile, info, ct).ConfigureAwait(false);
        _ui.Post(() => tile.ShowGit(lines));

        // A worktree added next to the repository after the registration is a child root from here on, so the chat started
        // in it lands on this tile within one refresh; a removed one stops being one. Git that fails to answer changes nothing.
        // The process runs only where git records a linked worktree, or one is registered and may have been removed.
        if (!info.IsRepository || (tile.Workspace.Worktrees.Count == 0 && !_git.HasLinkedWorktrees(tile.RootPath)))
        {
            return;
        }

        var porcelain = await _git.RunAsync(tile.RootPath, "worktree list --porcelain", ct).ConfigureAwait(false);
        if (porcelain is not null)
        {
            await _registry.UpdateWorktreesAsync(tile.Workspace, WorkspaceProbe.ParseWorktreeList(porcelain, tile.RootPath), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A line for the root folder, and one more for each other repository the tile's workspace file lists a folder of
    /// (Diffusion-Full: DiffusionNexus.Installer.SDK and DiffusionNexus), each under the name the file gives the folder, or
    /// its own. A folder that is no repository, or in a checkout already shown, adds none. Never throws but for
    /// cancellation, so the other folders cannot hold back the root's line or the worktree sync: a folder whose check
    /// fails, and a file that cannot be read for a moment, keep last round's lines. Runs off the UI thread: it reads the file.
    /// </summary>
    private async Task<IReadOnlyList<GitLine>> GitLinesAsync(WorkspaceTileViewModel tile, GitInfo root, CancellationToken ct)
    {
        var previous = tile.GitLines;
        var folders = tile.Workspace.WorkspaceFile is { } file ? WorkspaceProbe.FoldersOf(file) : [];
        if (folders is null)
        {
            return [previous[0] with { Branch = root.Branch, DirtyCount = root.DirtyCount, Path = tile.RootPath }, .. previous.Skip(1)];
        }

        var rootKey = PathNormalizer.Normalize(tile.RootPath);
        var rootLine = new GitLine(
            folders.FirstOrDefault(f => PathNormalizer.Normalize(f.Path) == rootKey)?.Label ?? WorkspaceProbe.FolderName(tile.RootPath),
            root.Branch,
            root.DirtyCount,
            tile.RootPath);
        var others = folders.Where(f => PathNormalizer.Normalize(f.Path) != rootKey).ToList();

        // Each folder's git directory first, which reads a file or two, so git status runs once per checkout, not once per
        // folder in it. Both steps look at the folders side by side: one on an offline share blocks Directory.Exists for
        // about 20 s, and that should cost the round once, not once per folder.
        var gitDirs = await Task.WhenAll(others.Select(f => GitDirOfAsync(f, ct))).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.GitDir is { } rootGitDir)
        {
            seen.Add(rootGitDir);
        }

        var lines = await Task.WhenAll(others.Zip(gitDirs).Select(p => p.Second switch
        {
            { Failed: true } => Task.FromResult(PreviousLine(p.First)),
            { GitDir: { } gitDir } when seen.Add(gitDir) => LineAsync(p.First),
            _ => Task.FromResult<GitLine?>(null),
        })).ConfigureAwait(false);

        List<GitLine> all = [rootLine, .. lines.OfType<GitLine>()];
        return all.Count > 1 ? all : [rootLine with { Folder = null }];

        GitLine? PreviousLine(WorkspaceFolder folder) =>
            previous.FirstOrDefault(l => l.Path is { } path && PathNormalizer.Normalize(path) == PathNormalizer.Normalize(folder.Path));

        async Task<GitLine?> LineAsync(WorkspaceFolder folder)
        {
            try
            {
                var info = await _git.InspectAsync(folder.Path, ct).ConfigureAwait(false);
                return new GitLine(folder.Label, info.Branch, info.DirtyCount, folder.Path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Git refresh of {Folder}, a folder of {Root}, failed", folder.Path, tile.RootPath);
                return PreviousLine(folder);
            }
        }
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

    [RelayCommand]
    private Task RefreshGit() => RefreshGitAsync(CancellationToken.None);

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

        var tile = snapshot.WorkspaceId is { } workspaceId ? FindTile(workspaceId) : null;
        foreach (var other in Tiles.Where(t => t != tile && t.Knows(snapshot.SessionId)).ToList())
        {
            other.Remove(snapshot.SessionId);
        }

        tile?.Upsert(snapshot, _pricing.Pricing);
        if (tile?.Chats.FirstOrDefault(c => c.SessionId == snapshot.SessionId) is { } row)
        {
            row.VoiceLabel = _voiceLabels.GetValueOrDefault(snapshot.SessionId);
        }
        if (NeedsMeFirst)
        {
            Resort();
        }
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

        var group = Tracks.FirstOrDefault(t => t.Id == workspace.TrackId);
        if (group is null)
        {
            group = new TrackGroupViewModel(new Track { Id = workspace.TrackId, Name = "Track", SortOrder = int.MaxValue });
            Tracks.Add(group);
            _ = ReloadTrackNamesAsync();
        }

        var tile = new WorkspaceTileViewModel(workspace, this);
        var index = group.Tiles.TakeWhile(t => string.Compare(t.Name, tile.Name, StringComparison.OrdinalIgnoreCase) < 0).Count();
        group.Tiles.Insert(index, tile);
        foreach (var snapshot in _engine.Snapshots.Where(s => s.WorkspaceId == workspace.Id && !IsClosed(s)))
        {
            tile.Upsert(snapshot, _pricing.Pricing);
        }

        TilesChanged?.Invoke();
        _ = RefreshGitAsync(CancellationToken.None);
    }

    /// <summary>Names the groups of new tracks in place: the groups and their tiles stay, whenever this runs.</summary>
    private async Task ReloadTrackNamesAsync()
    {
        try
        {
            var tracks = await _store.GetTracksAsync(CancellationToken.None);
            _ui.Post(() =>
            {
                var byId = tracks.ToDictionary(t => t.Id);
                foreach (var group in Tracks)
                {
                    if (byId.TryGetValue(group.Id, out var track))
                    {
                        group.Track = track;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            // Fire-and-forget from AddTile: a failed read would otherwise leave no trace, and the group its placeholder name.
            _logger.LogError(ex, "Reading the tracks for a new group failed; the group keeps its placeholder name");
        }
    }

    private void RemoveTile(Guid workspaceId)
    {
        foreach (var group in Tracks)
        {
            var tile = group.Tiles.FirstOrDefault(t => t.Id == workspaceId);
            if (tile is not null)
            {
                group.Tiles.Remove(tile);
                TilesChanged?.Invoke();
                TileRemoved?.Invoke(workspaceId);
                return;
            }
        }
    }

    private void Resort()
    {
        var orderedGroups = NeedsMeFirst
            ? Tracks.OrderBy(g => g.Tiles.Count == 0 ? 2 : g.Tiles.Min(t => t.AttentionRank)).ThenBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList()
            : Tracks.OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        for (var i = 0; i < orderedGroups.Count; i++)
        {
            var currentIndex = Tracks.IndexOf(orderedGroups[i]);
            if (currentIndex != i)
            {
                Tracks.Move(currentIndex, i);
            }
        }

        foreach (var group in Tracks)
        {
            var ordered = NeedsMeFirst
                ? group.Tiles.OrderBy(t => t.AttentionRank).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : group.Tiles.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var current = group.Tiles.IndexOf(ordered[i]);
                if (current != i)
                {
                    group.Tiles.Move(current, i);
                }
            }
        }

        OnPropertyChanged(nameof(Tiles));
    }

    public void Dispose()
    {
        _tickTimer?.Dispose();
        _gitTimer?.Dispose();
        _tabsTimer?.Dispose();
        _spotlightTimer?.Dispose();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }
}
