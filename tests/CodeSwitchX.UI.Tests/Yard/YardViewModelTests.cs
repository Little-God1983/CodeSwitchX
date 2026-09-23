using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.Yard;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Yard;

public class YardViewModelTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly IWorkspaceStore _store = Substitute.For<IWorkspaceStore>();
    private readonly WorkspaceResolver _resolver = new();
    private readonly SessionEngine _engine;
    private readonly Track _general = new() { Name = "General", SortOrder = 0 };
    private readonly Track _clients = new() { Name = "Clients", SortOrder = 1 };
    private readonly Workspace _app;
    private readonly Workspace _shop;
    private readonly YardViewModel _yard;

    public YardViewModelTests()
    {
        _app = new Workspace { Name = "App", RootPath = @"c:\repo\app", TrackId = _general.Id };
        _shop = new Workspace { Name = "Shop", RootPath = @"c:\repo\shop", TrackId = _clients.Id };
        _store.GetTracksAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Track>>([_general, _clients]));
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([_app, _shop]));
        _engine = new SessionEngine(_bus, _resolver, _time, NullLogger<SessionEngine>.Instance);
        var pricing = Substitute.For<IPricingProvider>();
        pricing.Pricing.Returns(PricingTable.Default);
        var registry = new WorkspaceRegistry(_store, _resolver, _bus);
        var git = new GitInspector((_, _, _) => Task.FromResult<string?>(null));
        _yard = new YardViewModel(_store, registry, _engine, pricing, git, _bus, new ImmediateDispatcher(), _time, NullLogger<YardViewModel>.Instance);
    }

    private SessionSnapshot Snapshot(string id, Guid workspaceId, SessionState state) => new()
    {
        SessionId = id, WorkspaceId = workspaceId, State = state, StartedAt = _time.GetUtcNow(), LastEventAt = _time.GetUtcNow(), StateSince = _time.GetUtcNow(), Title = "T " + id,
    };

    [Fact]
    public async Task Initialize_builds_track_groups_with_their_tiles()
    {
        await _yard.InitializeAsync(CancellationToken.None);

        _yard.Tracks.Select(t => t.Name).ShouldBe(["General", "Clients"]);
        _yard.Tracks[0].Tiles.ShouldHaveSingleItem().Name.ShouldBe("App");
        _yard.Tracks[1].Tiles.ShouldHaveSingleItem().Name.ShouldBe("Shop");
        _yard.Tiles.Select(t => t.Name).ShouldBe(["App", "Shop"]);
    }

    [Fact]
    public async Task Session_changes_land_on_the_owning_tile_and_raise_attention()
    {
        await _yard.InitializeAsync(CancellationToken.None);

        _bus.Publish(new SessionChanged(null, Snapshot("s1", _shop.Id, SessionState.Working)));
        _bus.Publish(new SessionChanged(null, Snapshot("s2", _shop.Id, SessionState.Waiting)));
        _bus.Publish(new SessionChanged(null, Snapshot("s1", _shop.Id, SessionState.Idle)));

        var shop = _yard.FindTile(_shop.Id)!;
        shop.Chats.Select(c => c.SessionId).ShouldBe(["s1", "s2"]);
        shop.Chats[0].State.ShouldBe(SessionState.Idle);
        shop.NeedsAttention.ShouldBeTrue();
        _yard.FindTile(_app.Id)!.NeedsAttention.ShouldBeFalse();
    }

    [Fact]
    public async Task Needs_me_first_puts_waiting_tiles_at_the_front()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        _bus.Publish(new SessionChanged(null, Snapshot("s2", _shop.Id, SessionState.Waiting)));

        _yard.NeedsMeFirst = true;

        _yard.Tiles.Select(t => t.Name).ShouldBe(["Shop", "App"]);
        _yard.Tracks.Select(t => t.Name).ShouldBe(["Clients", "General"], "the track with a waiting chat moves up");
        _yard.Tracks.Single(t => t.Name == "General").Tiles.Select(t => t.Name).ShouldBe(["App"], "tiles stay inside their track group");
    }

    [Fact]
    public async Task Ended_chats_disappear_ten_minutes_after_they_end()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        _bus.Publish(new SessionChanged(null, Snapshot("s1", _app.Id, SessionState.Ended)));

        _time.Advance(TimeSpan.FromMinutes(9));
        _yard.Tick(_time.GetUtcNow());
        _yard.FindTile(_app.Id)!.Chats.Count.ShouldBe(1);

        _time.Advance(TimeSpan.FromMinutes(2));
        _yard.Tick(_time.GetUtcNow());
        _yard.FindTile(_app.Id)!.Chats.ShouldBeEmpty();
    }

    [Fact]
    public async Task Registered_and_unregistered_workspaces_update_the_groups()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        var extra = new Workspace { Name = "Extra", RootPath = @"c:\repo\extra", TrackId = _general.Id };

        _bus.Publish(new WorkspaceRegistered(extra));
        _yard.Tracks[0].Tiles.Select(t => t.Name).ShouldBe(["App", "Extra"]);

        _bus.Publish(new WorkspaceUnregistered(_app.Id));
        _yard.Tracks[0].Tiles.Select(t => t.Name).ShouldBe(["Extra"]);
    }

    [Fact]
    public async Task Host_state_changes_are_reflected_on_the_tile()
    {
        await _yard.InitializeAsync(CancellationToken.None);

        _bus.Publish(new HostStateChanged(_app.Id, HostState.Running, 42, null));

        _yard.FindTile(_app.Id)!.HostState.ShouldBe(HostState.Running);
    }

    [Fact]
    public async Task Engine_snapshots_present_at_startup_are_shown()
    {
        _engine.Restore([Snapshot("old", _app.Id, SessionState.Idle)]);

        await _yard.InitializeAsync(CancellationToken.None);

        _yard.FindTile(_app.Id)!.Chats.ShouldHaveSingleItem().SessionId.ShouldBe("old");
    }
}
