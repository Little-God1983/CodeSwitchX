using System.Collections.ObjectModel;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting;
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
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Lock _gitGate = new();
    private ITimer? _tickTimer;
    private ITimer? _gitTimer;
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

    public YardViewModel(IWorkspaceStore store, WorkspaceRegistry registry, SessionEngine engine, IPricingProvider pricing, GitInspector git,
        IEventBus bus, IUiDispatcher ui, TimeProvider time, ILogger<YardViewModel> logger)
    {
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
    public event Action? AddWorkspaceRequested;

    /// <summary>A tile left the board (its workspace was unregistered): the Cab cannot show it any more.</summary>
    public event Action<Guid>? TileRemoved;

    /// <summary>A tile's VS Code window, open until now, is gone: closed by the user, or taken over by another folder.</summary>
    public event Action<Guid>? HostStopped;

    public async Task InitializeAsync(CancellationToken ct)
    {
        var tracks = await _store.GetTracksAsync(ct);
        var workspaces = await _store.GetAllAsync(ct);
        Tracks.Clear();
        // Ordinal, ignoring case, like AddTile and Resort: the jump keys follow the order shown, so it must not change with "Needs me first".
        foreach (var track in tracks.OrderBy(t => t.SortOrder).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var group = new TrackGroupViewModel(track);
            foreach (var workspace in workspaces.Where(w => w.TrackId == track.Id).OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase))
            {
                group.Tiles.Add(new WorkspaceTileViewModel(workspace, this));
            }

            Tracks.Add(group);
        }

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

        _tickTimer = _time.CreateTimer(_ => _ui.Post(() => Tick(_time.GetUtcNow())), null, TickInterval, TickInterval);
        _ = RefreshGitAsync(CancellationToken.None);
        // The tick asks for nothing while a round runs: a round longer than the interval would otherwise repeat back to back.
        _gitTimer = _time.CreateTimer(_ => _ = RequestGitRefresh(CancellationToken.None, again: false), null, GitRefreshInterval, GitRefreshInterval);
    }

    public WorkspaceTileViewModel? FindTile(Guid workspaceId) => Tiles.FirstOrDefault(t => t.Id == workspaceId);

    public void RequestOpen(Guid workspaceId) => OpenRequested?.Invoke(workspaceId);

    public async Task UnregisterAsync(Guid workspaceId)
    {
        try
        {
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
        _ui.Post(() =>
        {
            tile.Branch = info.Branch;
            tile.DirtyCount = info.DirtyCount;
        });

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
    private async Task<List<WorkspaceTileViewModel>> TilesOnUiThreadAsync()
    {
        var tiles = new TaskCompletionSource<List<WorkspaceTileViewModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(() =>
        {
            try
            {
                tiles.SetResult(Tiles.ToList());
            }
            catch (Exception ex)
            {
                tiles.TrySetException(ex);
            }
        });
        return await tiles.Task.WaitAsync(UiTimeout).ConfigureAwait(false);
    }

    [RelayCommand]
    private void AddWorkspace() => AddWorkspaceRequested?.Invoke();

    [RelayCommand]
    private Task RefreshGit() => RefreshGitAsync(CancellationToken.None);

    partial void OnNeedsMeFirstChanged(bool value) => Resort();

    /// <summary>
    /// A chat is shown on the tile of the workspace the engine maps it to, and on no other: a row left on a tile the
    /// engine has moved it away from (another workspace, or none when its cwd left every root) would never change again.
    /// </summary>
    internal void Apply(SessionSnapshot snapshot)
    {
        var tile = snapshot.WorkspaceId is { } workspaceId ? FindTile(workspaceId) : null;
        foreach (var other in Tiles.Where(t => t != tile && t.Chats.Any(c => c.SessionId == snapshot.SessionId)))
        {
            other.Remove(snapshot.SessionId);
        }

        tile?.Upsert(snapshot, _pricing.Pricing);
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
        foreach (var snapshot in _engine.Snapshots.Where(s => s.WorkspaceId == workspace.Id))
        {
            tile.Upsert(snapshot, _pricing.Pricing);
        }

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
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }
}
