using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting;
using CodeSwitchX.Telemetry;
using CodeSwitchX.Tests;
using CodeSwitchX.UI.Yard;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Yard;

public class YardViewModelTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "csx-yard-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly IWorkspaceStore _store = Substitute.For<IWorkspaceStore>();
    private readonly WorkspaceResolver _resolver = new();
    private readonly SessionEngine _engine;
    private readonly Track _general = new() { Name = "General", SortOrder = 0 };
    private readonly Track _clients = new() { Name = "Clients", SortOrder = 1 };
    private readonly Workspace _app;
    private readonly Workspace _shop;
    private readonly WorkspaceRegistry _registry;
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
        _registry = new WorkspaceRegistry(_store, _resolver, _bus);
        var git = new GitInspector((_, _, _) => Task.FromResult<string?>(null));
        _yard = new YardViewModel(_store, _registry, _engine, pricing, git, _bus, new ImmediateDispatcher(), _time, NullLogger<YardViewModel>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    /// <summary>A folder with a .git/HEAD on main, which is all GitInspector reads from disk.</summary>
    private string Repo(string name)
    {
        var root = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/main\n");
        return root;
    }

    /// <summary>A Yard over the same stores, engine and bus, with its own git runner and logger.</summary>
    private YardViewModel Yard(Func<string, string, CancellationToken, Task<string?>> runGit, ILogger<YardViewModel>? logger = null)
    {
        var pricing = Substitute.For<IPricingProvider>();
        pricing.Pricing.Returns(PricingTable.Default);
        return new YardViewModel(_store, _registry, _engine, pricing, new GitInspector(runGit), _bus, new ImmediateDispatcher(), _time, logger ?? NullLogger<YardViewModel>.Instance);
    }

    private SessionSnapshot Snapshot(string id, Guid workspaceId, SessionState state) => new()
    {
        SessionId = id, WorkspaceId = workspaceId, State = state, StartedAt = _time.GetUtcNow(), LastEventAt = _time.GetUtcNow(), StateSince = _time.GetUtcNow(), Title = "T " + id,
    };

    private HookEvent Hook(string sessionId, string eventName, SessionSignal signal, string cwd) => new()
    {
        SessionId = sessionId, EventName = eventName, Signal = signal, At = _time.GetUtcNow(), Cwd = cwd,
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

    [Fact]
    public async Task Stale_chats_leave_the_tile_after_the_stale_row_lifetime_and_return_on_new_activity()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        _bus.Publish(new SessionChanged(null, Snapshot("s1", _app.Id, SessionState.Stale)));

        _time.Advance(TimeSpan.FromMinutes(29));
        _yard.Tick(_time.GetUtcNow());
        _yard.FindTile(_app.Id)!.Chats.Count.ShouldBe(1);

        _time.Advance(TimeSpan.FromMinutes(2));
        _yard.Tick(_time.GetUtcNow());
        _yard.FindTile(_app.Id)!.Chats.ShouldBeEmpty();

        _bus.Publish(new SessionChanged(null, Snapshot("s1", _app.Id, SessionState.Working)));
        _yard.FindTile(_app.Id)!.Chats.ShouldHaveSingleItem().State.ShouldBe(SessionState.Working);
    }

    [Fact]
    public async Task A_chat_that_moves_out_of_every_workspace_leaves_the_tile_it_was_on()
    {
        _resolver.SetRoots(WorkspaceResolver.RootsOf([_app, _shop]));
        await _yard.InitializeAsync(CancellationToken.None);
        _engine.Apply(Hook("s1", "UserPromptSubmit", SessionSignal.PromptSubmit, @"c:\repo\app"));
        _yard.FindTile(_app.Id)!.Chats.ShouldHaveSingleItem().State.ShouldBe(SessionState.Working);

        // A cd into a folder outside every workspace (one added with --add-dir): the engine maps the chat to none.
        _engine.Apply(Hook("s1", "Notification", SessionSignal.Notification, @"c:\notes"));

        _engine.Get("s1")!.WorkspaceId.ShouldBeNull();
        _yard.FindTile(_app.Id)!.Chats.ShouldBeEmpty("a row left on App never changes again: no pulse when the chat waits, and a Working row is never removed");
    }

    [Fact]
    public async Task A_chat_mapped_to_a_workspace_without_a_tile_leaves_the_tile_it_was_on()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        _bus.Publish(new SessionChanged(null, Snapshot("s1", _app.Id, SessionState.Working)));

        _bus.Publish(new SessionChanged(null, Snapshot("s1", Guid.NewGuid(), SessionState.Waiting)));

        _yard.FindTile(_app.Id)!.Chats.ShouldBeEmpty("a row left on App would never change again");
    }

    [Fact]
    public async Task With_needs_me_first_on_a_track_whose_waiting_chat_leaves_every_workspace_moves_back_at_once()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        var waiting = Snapshot("s1", _shop.Id, SessionState.Waiting);
        _bus.Publish(new SessionChanged(null, waiting));
        _yard.NeedsMeFirst = true;
        _yard.Tracks.Select(t => t.Name).ShouldBe(["Clients", "General"]);

        _bus.Publish(new SessionChanged(waiting, waiting with { WorkspaceId = null }));

        _yard.Tracks.Select(t => t.Name).ShouldBe(["General", "Clients"], "the jump keys follow this order, so it must not wait for the next tick");
    }

    [Fact]
    public async Task A_hook_fed_chat_that_leaves_every_workspace_still_counts_as_proof_that_hooks_work()
    {
        _resolver.SetRoots(WorkspaceResolver.RootsOf([_app, _shop]));
        await _yard.InitializeAsync(CancellationToken.None);
        _engine.Apply(Hook("s1", "UserPromptSubmit", SessionSignal.PromptSubmit, @"c:\repo\app"));
        _engine.Apply(new TranscriptUpdate
        {
            SessionId = "s2", TranscriptPath = @"c:\t\s2.jsonl", ObservedAt = _time.GetUtcNow(), LastActivityAt = _time.GetUtcNow(), Cwd = @"c:\repo\shop",
            InferredSignal = SessionSignal.PromptSubmit,
        }); // a chat started before the hooks were installed
        _yard.Tick(_time.GetUtcNow());
        _yard.HooksInferredOnly.ShouldBeFalse();

        _engine.Apply(Hook("s1", "Notification", SessionSignal.Notification, @"c:\notes"));
        _yard.Tick(_time.GetUtcNow());

        _yard.HooksInferredOnly.ShouldBeFalse("s1 still reports through the hooks; it is only shown on no tile");
    }

    [Fact]
    public async Task Registering_a_workspace_inside_another_moves_its_chats_onto_the_new_tile()
    {
        _resolver.SetRoots(WorkspaceResolver.RootsOf([_app, _shop]));
        _engine.Start();
        await _yard.InitializeAsync(CancellationToken.None);
        _engine.Apply(Hook("s1", "UserPromptSubmit", SessionSignal.PromptSubmit, @"c:\repo\app\api"));
        _yard.FindTile(_app.Id)!.Chats.ShouldHaveSingleItem();
        var api = new Workspace { Name = "Api", RootPath = @"c:\repo\app\api", TrackId = _general.Id };
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([_app, _shop, api]));

        await _registry.RegisterAsync(api, CancellationToken.None);

        _yard.FindTile(api.Id)!.Chats.ShouldHaveSingleItem().SessionId.ShouldBe("s1");
        _yard.FindTile(_app.Id)!.Chats.ShouldBeEmpty("a chat is shown on one tile only");
    }

    [Fact]
    public async Task A_workspace_registered_into_a_new_track_gets_a_group_under_that_tracks_name()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        var tools = new Track { Name = "Tools", SortOrder = 2 };
        var cli = new Workspace { Name = "Cli", RootPath = @"c:\repo\cli", TrackId = tools.Id };
        _store.GetTracksAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Track>>([_general, _clients, tools]));

        _bus.Publish(new WorkspaceRegistered(cli));

        _yard.Tracks.Select(t => t.Name).ShouldBe(["General", "Clients", "Tools"]);
        _yard.Tracks[2].Tiles.ShouldHaveSingleItem().Name.ShouldBe("Cli");
    }

    [Fact]
    public async Task Tiles_keep_their_order_when_needs_me_first_is_turned_on_and_off()
    {
        // The start sorted with the culture comparer, AddTile and Resort ordinal ignoring case: "_tools" and "Zeta" swapped.
        var tools = new Workspace { Name = "_tools", RootPath = @"c:\repo\_tools", TrackId = _general.Id };
        var zeta = new Workspace { Name = "Zeta", RootPath = @"c:\repo\zeta", TrackId = _general.Id };
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([_app, _shop, tools, zeta]));
        await _yard.InitializeAsync(CancellationToken.None);
        var atStart = _yard.Tracks[0].Tiles.Select(t => t.Name).ToList();

        _yard.NeedsMeFirst = true;
        _yard.NeedsMeFirst = false;

        _yard.Tracks[0].Tiles.Select(t => t.Name).ShouldBe(atStart, "the jump keys follow the order shown, so it must not change with the checkbox");
        atStart.ShouldBe(["App", "Zeta", "_tools"], "one comparer everywhere: ordinal, ignoring case");
    }

    [Fact]
    public async Task A_track_list_that_cannot_be_read_when_a_tile_lands_in_a_new_track_is_logged_not_lost()
    {
        var log = new ListLogger<YardViewModel>();
        var yard = Yard((_, _, _) => Task.FromResult<string?>(null), log);
        await yard.InitializeAsync(CancellationToken.None);
        _store.GetTracksAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<IReadOnlyList<Track>>(new IOException("database is locked")));
        var cli = new Workspace { Name = "Cli", RootPath = @"c:\repo\cli", TrackId = Guid.NewGuid() };

        _bus.Publish(new WorkspaceRegistered(cli));

        yard.FindTile(cli.Id).ShouldNotBeNull("the tile is there, under a placeholder track name");
        log.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Message.Contains("track"), "a faulted task nobody awaits leaves no trace otherwise");
    }

    [Fact]
    public async Task A_tile_whose_git_check_throws_does_not_stop_the_refresh_of_the_tiles_after_it()
    {
        var app = new Workspace { Name = "App", RootPath = Repo("app"), TrackId = _general.Id };
        var shop = new Workspace { Name = "Shop", RootPath = Repo("shop"), TrackId = _general.Id };
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([app, shop]));
        var yard = Yard((dir, _, _) => dir == app.RootPath ? throw new InvalidOperationException("git hung") : Task.FromResult<string?>(string.Empty));
        await yard.InitializeAsync(CancellationToken.None);

        await yard.RefreshGitAsync(CancellationToken.None);

        yard.FindTile(shop.Id)!.Branch.ShouldBe("main", "one tile's failure is its own, every cycle");
        yard.FindTile(shop.Id)!.DirtyCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_workspace_added_during_a_git_refresh_is_refreshed_as_soon_as_that_refresh_ends()
    {
        var app = new Workspace { Name = "App", RootPath = Repo("app"), TrackId = _general.Id };
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([app]));
        var appStatusMayEnd = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var yard = Yard((dir, args, _) => dir == app.RootPath && args.StartsWith("status", StringComparison.Ordinal) ? appStatusMayEnd.Task : Task.FromResult<string?>(string.Empty));
        await yard.InitializeAsync(CancellationToken.None);
        var running = yard.RefreshGitAsync(CancellationToken.None); // waits in App's git status
        var extra = new Workspace { Name = "Extra", RootPath = Repo("extra"), TrackId = _general.Id };

        _bus.Publish(new WorkspaceRegistered(extra)); // the tile asks for a refresh while one runs
        appStatusMayEnd.SetResult(string.Empty);
        await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        yard.FindTile(extra.Id)!.Branch.ShouldBe("main", "the refresh asked for during the running one was dropped, and the tile said no git for 30 s");
    }

    [Fact]
    public async Task The_hooks_banner_stays_away_once_the_installer_says_the_hooks_are_installed()
    {
        _resolver.SetRoots(WorkspaceResolver.RootsOf([_app, _shop]));
        await _yard.InitializeAsync(CancellationToken.None);
        var now = _time.GetUtcNow();
        _engine.Apply(new TranscriptUpdate
        {
            SessionId = "s2", TranscriptPath = @"c:\t\s2.jsonl", ObservedAt = now, LastActivityAt = now, Cwd = @"c:\repo\shop", InferredSignal = SessionSignal.PromptSubmit,
        }); // a chat that ran before Install hooks was clicked: it stays inferred until it sends a hook or ends
        _yard.Tick(now);
        _yard.HooksInferredOnly.ShouldBeTrue();

        _yard.HooksInstalled = true;
        _yard.Tick(now);

        _yard.HooksInferredOnly.ShouldBeFalse("the installer, not a chat that happens to send a hook, says whether the hooks are installed");
    }

    [Fact]
    public async Task A_worktree_added_after_registration_becomes_a_child_root_at_the_next_git_refresh()
    {
        var app = new Workspace { Name = "App", RootPath = Repo("app"), TrackId = _general.Id };
        var hotfix = Path.Combine(_tempRoot, "app-hotfix");
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([app]));
        _resolver.SetRoots(WorkspaceResolver.RootsOf([app]));
        var yard = Yard((_, args, _) => Task.FromResult<string?>(args.StartsWith("worktree list", StringComparison.Ordinal)
            ? $"worktree {app.RootPath}\nHEAD 1111111111111111111111111111111111111111\nbranch refs/heads/main\n\nworktree {hotfix}\nHEAD 2222222222222222222222222222222222222222\nbranch refs/heads/hotfix\n\n"
            : string.Empty));
        await yard.InitializeAsync(CancellationToken.None);

        await yard.RefreshGitAsync(CancellationToken.None);

        await _store.Received(1).UpdateAsync(app, Arg.Any<CancellationToken>());
        var worktree = app.Worktrees.ShouldHaveSingleItem();
        worktree.Path.ShouldBe(hotfix);
        worktree.Branch.ShouldBe("hotfix");
        _resolver.Resolve(Path.Combine(hotfix, "src")).ShouldBe(app.Id, "a chat started in the new worktree moves onto App's tile");

        await yard.RefreshGitAsync(CancellationToken.None);
        await _store.Received(1).UpdateAsync(app, Arg.Any<CancellationToken>()); // the same set again: nothing saved
    }
}
