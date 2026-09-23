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
    private ITimer? _tickTimer;
    private ITimer? _gitTimer;
    private int _gitRefreshRunning;

    [ObservableProperty] private bool _needsMeFirst;
    [ObservableProperty] private bool _hooksInferredOnly;

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

    public async Task InitializeAsync(CancellationToken ct)
    {
        var tracks = await _store.GetTracksAsync(ct);
        var workspaces = await _store.GetAllAsync(ct);
        Tracks.Clear();
        foreach (var track in tracks.OrderBy(t => t.SortOrder).ThenBy(t => t.Name))
        {
            var group = new TrackGroupViewModel(track);
            foreach (var workspace in workspaces.Where(w => w.TrackId == track.Id).OrderBy(w => w.Name))
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
        // Posted to the UI thread so the tile collections are only ever enumerated there.
        _gitTimer = _time.CreateTimer(_ => _ui.Post(() => _ = RefreshGitAsync(CancellationToken.None)), null, TimeSpan.Zero, GitRefreshInterval);
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

        HooksInferredOnly = Tiles.Any(t => t.HasInferredChats) && !Tiles.SelectMany(t => t.Chats).Any(c => !c.Inferred && c.IsLive);
        if (NeedsMeFirst)
        {
            Resort();
        }
    }

    public async Task RefreshGitAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _gitRefreshRunning, 1) == 1)
        {
            return;
        }

        try
        {
            foreach (var tile in Tiles.ToList())
            {
                var info = await _git.InspectAsync(tile.RootPath, ct).ConfigureAwait(false);
                _ui.Post(() =>
                {
                    tile.Branch = info.Branch;
                    tile.DirtyCount = info.DirtyCount;
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Git refresh failed");
        }
        finally
        {
            Volatile.Write(ref _gitRefreshRunning, 0);
        }
    }

    [RelayCommand]
    private void AddWorkspace() => AddWorkspaceRequested?.Invoke();

    [RelayCommand]
    private Task RefreshGit() => RefreshGitAsync(CancellationToken.None);

    partial void OnNeedsMeFirstChanged(bool value) => Resort();

    internal void Apply(SessionSnapshot snapshot)
    {
        if (snapshot.WorkspaceId is not { } workspaceId)
        {
            return;
        }

        var tile = FindTile(workspaceId);
        if (tile is null)
        {
            return;
        }

        foreach (var other in Tiles.Where(t => t.Id != workspaceId && t.Chats.Any(c => c.SessionId == snapshot.SessionId)))
        {
            other.Remove(snapshot.SessionId);
        }

        tile.Upsert(snapshot, _pricing.Pricing);
        if (NeedsMeFirst)
        {
            Resort();
        }
    }

    internal void Apply(HostStateChanged change)
    {
        var tile = FindTile(change.WorkspaceId);
        if (tile is not null)
        {
            tile.HostState = change.State;
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

    private async Task ReloadTrackNamesAsync()
    {
        var tracks = await _store.GetTracksAsync(CancellationToken.None);
        _ui.Post(() =>
        {
            var byId = tracks.ToDictionary(t => t.Id);
            foreach (var group in Tracks.ToList())
            {
                if (byId.TryGetValue(group.Id, out var track) && group.Track.Name != track.Name)
                {
                    var replacement = new TrackGroupViewModel(track);
                    foreach (var tile in group.Tiles)
                    {
                        replacement.Tiles.Add(tile);
                    }

                    Tracks[Tracks.IndexOf(group)] = replacement;
                }
            }
        });
    }

    private void RemoveTile(Guid workspaceId)
    {
        foreach (var group in Tracks)
        {
            var tile = group.Tiles.FirstOrDefault(t => t.Id == workspaceId);
            if (tile is not null)
            {
                group.Tiles.Remove(tile);
                return;
            }
        }
    }

    private void Resort()
    {
        var orderedGroups = NeedsMeFirst
            ? Tracks.OrderBy(g => g.Tiles.Count == 0 ? 2 : g.Tiles.Min(t => t.AttentionRank)).ThenBy(g => g.SortOrder).ThenBy(g => g.Name).ToList()
            : Tracks.OrderBy(g => g.SortOrder).ThenBy(g => g.Name).ToList();
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
