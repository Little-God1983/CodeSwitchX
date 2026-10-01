using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting;
using CodeSwitchX.Telemetry;
using CodeSwitchX.Tests;
using CodeSwitchX.UI.Infrastructure;
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

    [Theory]
    [InlineData(1.25, 1.25)]
    [InlineData(0.1, YardViewModel.MinTileScale)]
    [InlineData(9.0, YardViewModel.MaxTileScale)]
    [InlineData(double.NaN, 1)]
    [InlineData(0.9000000000000001, 0.9)]
    public void The_tile_size_stays_in_the_sliders_range_and_to_two_decimals(double set, double kept)
    {
        _yard.TileScale = set;

        _yard.TileScale.ShouldBe(kept);
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
    private YardViewModel Yard(Func<string, string, CancellationToken, Task<string?>> runGit, ILogger<YardViewModel>? logger = null, IUiDispatcher? ui = null)
    {
        var pricing = Substitute.For<IPricingProvider>();
        pricing.Pricing.Returns(PricingTable.Default);
        return new YardViewModel(_store, _registry, _engine, pricing, new GitInspector(runGit), _bus, ui ?? new ImmediateDispatcher(), _time, logger ?? NullLogger<YardViewModel>.Instance);
    }

    /// <summary>A UI thread that never gets to what is posted: the dispatcher has shut down, say.</summary>
    private sealed class DroppingDispatcher : IUiDispatcher
    {
        public void Post(Action action)
        {
        }
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
    public async Task A_session_that_starts_and_ends_without_a_prompt_never_gets_a_row()
    {
        _resolver.SetRoots(WorkspaceResolver.RootsOf([_app, _shop]));
        await _yard.InitializeAsync(CancellationToken.None);
        var app = _yard.FindTile(_app.Id)!;

        _engine.Apply(Hook("s1", "SessionStart", SessionSignal.SessionStart, @"c:\repo\app"));
        app.Chats.ShouldBeEmpty("an idle session that was never prompted is not a chat yet");

        // The second of the two sessions a VS Code window starts lived up to 77 s in the user's database.
        _time.Advance(TimeSpan.FromSeconds(77));
        _yard.Tick(_time.GetUtcNow());
        app.Chats.ShouldBeEmpty();

        _engine.Apply(Hook("s1", "SessionEnd", SessionSignal.SessionEnd, @"c:\repo\app"));
        app.Chats.ShouldBeEmpty("a session that never held a conversation is not a chat");
        app.AttentionRank.ShouldBe(2);
    }

    [Fact]
    public async Task A_prompt_typed_into_a_fresh_panel_gives_the_session_its_row_at_once()
    {
        _resolver.SetRoots(WorkspaceResolver.RootsOf([_app, _shop]));
        await _yard.InitializeAsync(CancellationToken.None);
        var app = _yard.FindTile(_app.Id)!;
        _engine.Apply(Hook("s1", "SessionStart", SessionSignal.SessionStart, @"c:\repo\app"));

        _engine.Apply(Hook("s1", "UserPromptSubmit", SessionSignal.PromptSubmit, @"c:\repo\app") with { Prompt = "fix the build" });

        var row = app.Chats.ShouldHaveSingleItem("the prompt shows the chat before its reply");
        row.State.ShouldBe(SessionState.Working);
        row.Title.ShouldBe("fix the build");
        app.AttentionRank.ShouldBe(1);
    }

    [Fact]
    public async Task A_chat_that_held_a_conversation_keeps_its_row_when_it_goes_idle()
    {
        _resolver.SetRoots(WorkspaceResolver.RootsOf([_app, _shop]));
        await _yard.InitializeAsync(CancellationToken.None);
        var app = _yard.FindTile(_app.Id)!;
        _engine.Apply(Hook("s1", "SessionStart", SessionSignal.SessionStart, @"c:\repo\app"));
        _engine.Apply(Hook("s1", "UserPromptSubmit", SessionSignal.PromptSubmit, @"c:\repo\app") with { Prompt = "fix the build" });

        _engine.Apply(Hook("s1", "Stop", SessionSignal.Stop, @"c:\repo\app"));

        app.Chats.ShouldHaveSingleItem().State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public async Task A_session_that_waited_for_the_user_without_a_conversation_loses_its_row_when_it_idles_again()
    {
        _resolver.SetRoots(WorkspaceResolver.RootsOf([_app, _shop]));
        await _yard.InitializeAsync(CancellationToken.None);
        var app = _yard.FindTile(_app.Id)!;
        _engine.Apply(Hook("s1", "SessionStart", SessionSignal.SessionStart, @"c:\repo\app"));

        _engine.Apply(Hook("s1", "Notification", SessionSignal.Notification, @"c:\repo\app"));
        app.Chats.ShouldHaveSingleItem("a chat that needs the user shows, prompted or not");
        app.NeedsAttention.ShouldBeTrue();

        _engine.Apply(Hook("s1", "Stop", SessionSignal.Stop, @"c:\repo\app"));
        app.Chats.ShouldBeEmpty();
        app.NeedsAttention.ShouldBeFalse();
    }

    [Fact]
    public async Task Ended_sessions_restored_at_startup_are_shown_only_if_they_held_a_conversation()
    {
        var ghost = Snapshot("ghost", _app.Id, SessionState.Ended) with { Title = null };
        _engine.Restore(
        [
            ghost,
            Snapshot("titled", _app.Id, SessionState.Ended),
            ghost with { SessionId = "replied", LatestContext = new TokenUsage(10, 20, 0, 0, 0) },
            ghost with { SessionId = "tooled", LastToolName = "Bash" },
            ghost with { SessionId = "errored", State = SessionState.Errored },
        ]);

        await _yard.InitializeAsync(CancellationToken.None);

        _yard.FindTile(_app.Id)!.Chats.Select(c => c.SessionId).ShouldBe(["titled", "replied", "tooled"], ignoreOrder: true);
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

        yard.FindTile(shop.Id)!.GitLines[0].Branch.ShouldBe("main", "one tile's failure is its own, every cycle");
        yard.FindTile(shop.Id)!.GitLines[0].DirtyCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("", "clean")]
    [InlineData(" M src/app.cs\n?? notes.md\n", "2 changed")]
    [InlineData(null, null)]
    public async Task The_tile_labels_its_git_state_clean_or_by_its_uncommitted_files_and_not_at_all_when_git_cannot_tell(string? status, string? label)
    {
        // "0 dirty" on a clean repository read as if something were wrong.
        var app = new Workspace { Name = "App", RootPath = Repo("app"), TrackId = _general.Id };
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([app]));
        var yard = Yard((_, _, _) => Task.FromResult(status));
        await yard.InitializeAsync(CancellationToken.None);

        await yard.RefreshGitAsync(CancellationToken.None);

        yard.FindTile(app.Id)!.GitLines.ShouldHaveSingleItem().GitStateLabel.ShouldBe(label);
    }

    [Fact]
    public async Task A_workspace_file_with_folders_in_several_repositories_gets_a_named_line_per_repository()
    {
        // Diffusion-Full lists DiffusionNexus.Installer.SDK and DiffusionNexus; changes in the second never showed.
        var sdk = Repo("sdk");
        var nexus = Repo("nexus");
        var docs = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(docs);
        Directory.CreateDirectory(Path.Combine(sdk, "tools"));
        var file = Path.Combine(_tempRoot, "Full.code-workspace");
        File.WriteAllText(file, """{ "folders": [ { "path": "sdk" }, { "path": "nexus" }, { "path": "docs" }, { "path": "sdk/tools" } ] }""");
        var full = new Workspace { Name = "Full", RootPath = sdk, WorkspaceFile = file, TrackId = _general.Id };
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([full]));
        var status = new List<string>();
        var yard = Yard((dir, args, _) =>
        {
            if (args.StartsWith("status", StringComparison.Ordinal))
            {
                lock (status)
                {
                    status.Add(dir);
                }
            }

            return Task.FromResult<string?>(dir == nexus ? " M a.cs\n M b.cs\n?? c.cs\n" : string.Empty);
        });
        await yard.InitializeAsync(CancellationToken.None);

        await yard.RefreshGitAsync(CancellationToken.None);

        var tile = yard.FindTile(full.Id)!;
        tile.GitLines.Select(l => (l.Text, l.GitStateLabel)).ShouldBe([("sdk · main", "clean"), ("nexus · main", "3 changed")],
            "docs is no repository, and sdk/tools is in sdk's");
        status.ShouldNotBeEmpty();
        status.Count(d => d == sdk).ShouldBe(status.Count(d => d == nexus), "git status runs once per checkout: sdk's line in the file, and sdk/tools, cost none");
        status.ShouldNotContain(Path.Combine(sdk, "tools"));
    }

    [Fact]
    public async Task A_git_round_that_finds_the_same_lines_leaves_the_tile_as_it_is()
    {
        // A new list every round rebuilt every line on the tile each 30 s, and closed the tooltip the user had open.
        var sdk = Repo("sdk");
        Repo("nexus");
        var file = Path.Combine(_tempRoot, "Full.code-workspace");
        File.WriteAllText(file, """{ "folders": [ { "path": "sdk" }, { "path": "nexus" } ] }""");
        var full = new Workspace { Name = "Full", RootPath = sdk, WorkspaceFile = file, TrackId = _general.Id };
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([full]));
        var yard = Yard((_, _, _) => Task.FromResult<string?>(string.Empty));
        await yard.InitializeAsync(CancellationToken.None);
        await yard.RefreshGitAsync(CancellationToken.None);
        var tile = yard.FindTile(full.Id)!;
        var changed = new List<string?>();
        tile.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await yard.RefreshGitAsync(CancellationToken.None);

        tile.GitLines.Count.ShouldBe(2);
        changed.ShouldNotContain(nameof(WorkspaceTileViewModel.GitLines));
    }

    [Fact]
    public async Task A_line_is_named_as_the_workspace_file_names_its_folder_and_its_tooltip_gives_the_path()
    {
        // Two folders called src in different repositories both read "src · main", tooltip included.
        var clientSrc = Path.Combine(Repo("client"), "src");
        var serverSrc = Path.Combine(Repo("server"), "src");
        Directory.CreateDirectory(clientSrc);
        Directory.CreateDirectory(serverSrc);
        var file = Path.Combine(_tempRoot, "Both.code-workspace");
        File.WriteAllText(file, """{ "folders": [ { "path": "client/src", "name": "Client" }, { "path": "server/src" } ] }""");
        var both = new Workspace { Name = "Both", RootPath = clientSrc, WorkspaceFile = file, TrackId = _general.Id };
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([both]));
        var yard = Yard((_, _, _) => Task.FromResult<string?>(string.Empty));
        await yard.InitializeAsync(CancellationToken.None);

        await yard.RefreshGitAsync(CancellationToken.None);

        var lines = yard.FindTile(both.Id)!.GitLines;
        lines.Select(l => l.Text).ShouldBe(["Client · main", "src · main"]);
        lines.Select(l => l.ToolTipText).ShouldBe([$"Client · main\n{clientSrc}", $"src · main\n{serverSrc}"]);
    }

    [Fact]
    public async Task A_folder_whose_check_fails_keeps_its_line_and_holds_back_neither_the_root_nor_the_worktree_sync()
    {
        var sdk = Repo("sdk");
        var nexus = Repo("nexus");
        var hotfix = Path.Combine(_tempRoot, "sdk-hotfix");
        var file = Path.Combine(_tempRoot, "Full.code-workspace");
        File.WriteAllText(file, """{ "folders": [ { "path": "sdk" }, { "path": "nexus" } ] }""");
        var full = new Workspace { Name = "Full", RootPath = sdk, WorkspaceFile = file, TrackId = _general.Id };
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([full]));
        var nexusFails = false;
        var yard = Yard((dir, args, _) =>
            dir == nexus && nexusFails ? throw new IOException("HEAD is being rewritten")
            : args.StartsWith("worktree list", StringComparison.Ordinal)
                ? Task.FromResult<string?>($"worktree {sdk}\nHEAD 1111111111111111111111111111111111111111\nbranch refs/heads/next\n\nworktree {hotfix}\nHEAD 2222222222222222222222222222222222222222\nbranch refs/heads/hotfix\n\n")
            : Task.FromResult<string?>(dir == nexus ? " M a.cs\n" : string.Empty));
        await yard.InitializeAsync(CancellationToken.None);
        await yard.RefreshGitAsync(CancellationToken.None);
        nexusFails = true;
        File.WriteAllText(Path.Combine(sdk, ".git", "HEAD"), "ref: refs/heads/next\n");
        Directory.CreateDirectory(Path.Combine(sdk, ".git", "worktrees", "sdk-hotfix"));

        await yard.RefreshGitAsync(CancellationToken.None);

        yard.FindTile(full.Id)!.GitLines.Select(l => (l.Text, l.GitStateLabel)).ShouldBe([("sdk · next", "clean"), ("nexus · main", "1 changed")],
            "the root's line is this round's, nexus keeps last round's rather than vanishing");
        full.Worktrees.ShouldHaveSingleItem().Path.ShouldBe(hotfix);
    }

    [Fact]
    public async Task A_workspace_file_that_cannot_be_read_for_a_moment_keeps_the_other_repositories_lines()
    {
        // VS Code rewrites the file on a settings change; a round that hit it then dropped the tile to one line for 30 s.
        var sdk = Repo("sdk");
        Repo("nexus");
        var file = Path.Combine(_tempRoot, "Full.code-workspace");
        File.WriteAllText(file, """{ "folders": [ { "path": "sdk" }, { "path": "nexus" } ] }""");
        var full = new Workspace { Name = "Full", RootPath = sdk, WorkspaceFile = file, TrackId = _general.Id };
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([full]));
        var yard = Yard((_, _, _) => Task.FromResult<string?>(string.Empty));
        await yard.InitializeAsync(CancellationToken.None);
        await yard.RefreshGitAsync(CancellationToken.None);
        File.WriteAllText(Path.Combine(sdk, ".git", "HEAD"), "ref: refs/heads/next\n");

        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await yard.RefreshGitAsync(CancellationToken.None);
        }

        yard.FindTile(full.Id)!.GitLines.Select(l => l.Text).ShouldBe(["sdk · next", "nexus · main"]);
    }

    [Fact]
    public async Task A_workspace_with_one_repository_shows_its_branch_without_a_folder_name()
    {
        var app = Repo("app");
        var file = Path.Combine(_tempRoot, "App.code-workspace");
        File.WriteAllText(file, """{ "folders": [ { "path": "app" } ] }""");
        var single = new Workspace { Name = "App", RootPath = app, WorkspaceFile = file, TrackId = _general.Id };
        var plain = new Workspace { Name = "Plain", RootPath = Repo("plain"), TrackId = _general.Id };
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([single, plain]));
        var yard = Yard((_, _, _) => Task.FromResult<string?>(string.Empty));
        await yard.InitializeAsync(CancellationToken.None);

        await yard.RefreshGitAsync(CancellationToken.None);

        yard.FindTile(single.Id)!.GitLines.ShouldHaveSingleItem().Text.ShouldBe("main");
        yard.FindTile(plain.Id)!.GitLines.ShouldHaveSingleItem().Text.ShouldBe("main");
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

        yard.FindTile(extra.Id)!.GitLines[0].Branch.ShouldBe("main", "the refresh asked for during the running one was dropped, and the tile said no git for 30 s");
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
        Directory.CreateDirectory(Path.Combine(app.RootPath, ".git", "worktrees", "app-hotfix")); // git's record of a linked worktree
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([app]));
        _resolver.SetRoots(WorkspaceResolver.RootsOf([app]));
        var yard = Yard((_, args, _) => Task.FromResult<string?>(args.StartsWith("worktree list", StringComparison.Ordinal)
            ? $"worktree {app.RootPath}\nHEAD 1111111111111111111111111111111111111111\nbranch refs/heads/main\n\nworktree {hotfix}\nHEAD 2222222222222222222222222222222222222222\nbranch refs/heads/hotfix\n\n"
            : string.Empty));
        await yard.InitializeAsync(CancellationToken.None);

        await yard.RefreshGitAsync(CancellationToken.None);

        await _store.Received(1).ReplaceWorktreesAsync(app.Id, Arg.Any<IReadOnlyList<Worktree>>(), Arg.Any<CancellationToken>());
        var worktree = app.Worktrees.ShouldHaveSingleItem();
        worktree.Path.ShouldBe(hotfix);
        worktree.Branch.ShouldBe("hotfix");
        _resolver.Resolve(Path.Combine(hotfix, "src")).ShouldBe(app.Id, "a chat started in the new worktree moves onto App's tile");

        await yard.RefreshGitAsync(CancellationToken.None);
        await _store.Received(1).ReplaceWorktreesAsync(app.Id, Arg.Any<IReadOnlyList<Worktree>>(), Arg.Any<CancellationToken>()); // the same set again: nothing saved
    }

    [Fact]
    public async Task No_worktree_process_runs_for_a_repository_without_linked_worktrees()
    {
        // git worktree list is a process per repository per round; git records linked worktrees under .git\worktrees, so
        // a repository without that folder, and without worktrees registered, needs none.
        var plain = new Workspace { Name = "Plain", RootPath = Repo("plain"), TrackId = _general.Id };
        var linked = new Workspace { Name = "Linked", RootPath = Repo("linked"), TrackId = _general.Id };
        Directory.CreateDirectory(Path.Combine(linked.RootPath, ".git", "worktrees", "feature"));
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([plain, linked]));
        var listed = new List<string>();
        var yard = Yard((dir, args, _) =>
        {
            if (args.StartsWith("worktree list", StringComparison.Ordinal))
            {
                lock (listed)
                {
                    listed.Add(dir);
                }
            }

            return Task.FromResult<string?>(string.Empty);
        });
        await yard.InitializeAsync(CancellationToken.None);

        await yard.RefreshGitAsync(CancellationToken.None);

        listed.ShouldNotBeEmpty();
        listed.ShouldAllBe(dir => dir == linked.RootPath, "the round the start ran and this one both list Linked alone");
    }

    [Fact]
    public async Task The_timers_tick_does_not_queue_another_round_while_one_still_runs()
    {
        // A round longer than the interval would otherwise repeat back to back, with git processes running all the time.
        var app = new Workspace { Name = "App", RootPath = Repo("app"), TrackId = _general.Id };
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([app]));
        var statusMayEnd = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statusCalls = 0;
        var yard = Yard((_, args, _) =>
        {
            if (!args.StartsWith("status", StringComparison.Ordinal))
            {
                return Task.FromResult<string?>(string.Empty);
            }

            Interlocked.Increment(ref statusCalls);
            return statusMayEnd.Task;
        });
        await yard.InitializeAsync(CancellationToken.None); // the first round starts and waits in git status
        _time.Advance(YardViewModel.GitRefreshInterval); // a tick while it runs
        _time.Advance(YardViewModel.GitRefreshInterval); // and another

        statusMayEnd.SetResult(string.Empty);
        await yard.CurrentGitRefresh.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        statusCalls.ShouldBe(1, "the ticks that found a round running are dropped; the next tick after it ends runs the next round");
    }

    [Fact]
    public async Task A_ui_thread_that_never_answers_does_not_stop_every_later_refresh()
    {
        var app = new Workspace { Name = "App", RootPath = Repo("app"), TrackId = _general.Id };
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([app]));
        var yard = Yard((_, _, _) => Task.FromResult<string?>(string.Empty), ui: new DroppingDispatcher());
        yard.UiTimeout = TimeSpan.FromMilliseconds(100);
        await yard.InitializeAsync(CancellationToken.None);

        await yard.RefreshGitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        yard.CurrentGitRefresh.IsCompleted.ShouldBeTrue("a round that cannot read the tiles ends, so the next one can run");
    }

    [Theory]
    [InlineData(@"c:\repo\shop\Shop.code-workspace")]
    [InlineData(@"c:\repo\shop\Shop.sln")]
    [InlineData(@"c:\repo\shop\Shop.slnx")]
    [InlineData(@"c:\repo\shop")]
    [InlineData(@"\\offline-server\share\x.code-workspace")]
    public void A_single_dropped_workspace_file_solution_or_folder_is_taken_as_the_path_to_add_without_asking_the_disk(string path)
    {
        // None of these exist: DragEnter runs on the UI thread, where an offline share would freeze the Yard and Explorer.
        YardViewModel.DroppedWorkspacePath([path]).ShouldBe(path);
    }

    [Fact]
    public void A_drop_the_add_workspace_dialog_cannot_detect_is_refused()
    {
        YardViewModel.DroppedWorkspacePath([@"c:\repo\shop\README.md"]).ShouldBeNull("only what the dialog detects is accepted");
        YardViewModel.DroppedWorkspacePath([@"c:\repo\shop\notes.txt"]).ShouldBeNull();
        YardViewModel.DroppedWorkspacePath([@"c:\repo\shop\Shop.code-workspace", @"c:\repo\shop"]).ShouldBeNull("the dialog adds one workspace at a time");
        YardViewModel.DroppedWorkspacePath([]).ShouldBeNull();
        YardViewModel.DroppedWorkspacePath(null).ShouldBeNull("the drag carries no files, text say");
    }

    [Fact]
    public void Add_workspace_asks_for_an_empty_dialog_and_a_drop_for_one_with_the_dropped_path()
    {
        var requested = new List<string?>();
        _yard.AddWorkspaceRequested += path => requested.Add(path);

        _yard.AddWorkspaceCommand.Execute(null);
        _yard.AddWorkspaceFrom(@"c:\repo\shop\Shop.code-workspace");

        requested.ShouldBe([null, @"c:\repo\shop\Shop.code-workspace"]);
    }

    [Fact]
    public async Task A_chat_Raven_started_is_marked_with_how_it_runs_when_its_row_comes_and_while_it_stays()
    {
        await _yard.InitializeAsync(CancellationToken.None);

        _yard.MarkVoice("s1", "Fable 5.1 · high");
        _bus.Publish(new SessionChanged(null, Snapshot("s1", _shop.Id, SessionState.Working) with { Model = "claude-fable-5-1" }));
        _bus.Publish(new SessionChanged(null, Snapshot("s2", _shop.Id, SessionState.Working)));

        var rows = _yard.FindTile(_shop.Id)!.Chats;
        rows.Single(r => r.SessionId == "s1").VoiceLabel.ShouldBe("Fable 5.1 · high");
        rows.Single(r => r.SessionId == "s1").IsVoice.ShouldBeTrue();
        rows.Single(r => r.SessionId == "s1").Model.ShouldBe("claude-fable-5-1");
        rows.Single(r => r.SessionId == "s2").IsVoice.ShouldBeFalse();

        _bus.Publish(new SessionChanged(null, Snapshot("s1", _shop.Id, SessionState.Idle) with { Version = 5 }));
        rows.Single(r => r.SessionId == "s1").IsVoice.ShouldBeTrue();
    }

    [Fact]
    public async Task A_chat_handed_over_to_VS_Code_loses_its_mark()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        _yard.MarkVoice("s1", "Opus 5.5 · max");
        _bus.Publish(new SessionChanged(null, Snapshot("s1", _shop.Id, SessionState.Working)));

        _yard.MarkVoice("s1", null);
        _bus.Publish(new SessionChanged(null, Snapshot("s1", _shop.Id, SessionState.Idle) with { Version = 5 }));

        _yard.FindTile(_shop.Id)!.Chats.Single().IsVoice.ShouldBeFalse();
    }

    [Fact]
    public async Task A_spotlit_tile_is_lit_for_a_moment_and_only_one_at_a_time()
    {
        await _yard.InitializeAsync(CancellationToken.None);

        _yard.Spotlight(_app.Id).ShouldBeTrue();
        _yard.Spotlight(_shop.Id).ShouldBeTrue();

        _yard.FindTile(_app.Id)!.IsSpotlit.ShouldBeFalse();
        _yard.FindTile(_shop.Id)!.IsSpotlit.ShouldBeTrue();
        _time.Advance(YardViewModel.SpotlightTime);
        _yard.FindTile(_shop.Id)!.IsSpotlit.ShouldBeFalse();
        _yard.Spotlight(Guid.NewGuid()).ShouldBeFalse("no such tile");
    }
}
