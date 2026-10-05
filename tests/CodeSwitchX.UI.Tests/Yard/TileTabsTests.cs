using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.Yard;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>#164: a tile shows the chat tabs open in its workspace's VS Code window, and the chats that run.</summary>
public sealed class TileTabsTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly IWorkspaceStore _store = Substitute.For<IWorkspaceStore>();
    private readonly WorkspaceResolver _resolver = new();
    private readonly Track _general = new() { Name = "General" };
    private readonly Workspace _app;
    private readonly Workspace _shop;
    private readonly SessionEngine _engine;
    private readonly FakeTabs _tabs = new();
    private readonly Dictionary<string, DateTimeOffset> _writtenIn = [];
    private readonly YardViewModel _yard;

    public TileTabsTests()
    {
        _app = new Workspace { Name = "App", RootPath = @"c:\repo\app", TrackId = _general.Id };
        _shop = new Workspace { Name = "Shop", RootPath = @"c:\repo\shop", TrackId = _general.Id };
        _store.GetTracksAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Track>>([_general]));
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([_app, _shop]));
        _engine = new SessionEngine(_bus, _resolver, _time, NullLogger<SessionEngine>.Instance);
        var pricing = Substitute.For<IPricingProvider>();
        pricing.Pricing.Returns(PricingTable.Default);
        _yard = new YardViewModel(_store, new WorkspaceRegistry(_store, _resolver, _bus), _engine, pricing, new GitInspector((_, _, _) => Task.FromResult<string?>(null)),
            _bus, new ImmediateDispatcher(), _time, NullLogger<YardViewModel>.Instance, _tabs, id => _writtenIn.TryGetValue(id, out var at) ? at : null);
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private WorkspaceTileViewModel App => _yard.FindTile(_app.Id)!;

    private string[] Rows => [.. App.Chats.Select(c => c.SessionId)];

    private SessionSnapshot Chat(string id, SessionState state, string? title = "T", Guid? workspace = null) => new()
    {
        SessionId = id, WorkspaceId = workspace ?? _app.Id, State = state, StartedAt = Now, LastEventAt = Now, StateSince = Now, Title = title is null ? null : $"{title} {id}",
    };

    private void Says(SessionSnapshot chat) => _bus.Publish(new SessionChanged(null, chat));

    /// <summary>VS Code writes the tabs of the App window down, now.</summary>
    private Task VsCodeWrites(params OpenChatTab[] tabs)
    {
        _tabs.Of[_app.Id] = new OpenChatTabs(Now, tabs);
        return _yard.RefreshTabsAsync();
    }

    private static OpenChatTab Tab(string id, string? title = null) => new(id, title);

    [Fact]
    public async Task A_tab_VS_Code_will_come_back_with_shows_before_its_chat_ever_ran()
    {
        _tabs.Of[_app.Id] = new OpenChatTabs(Now - TimeSpan.FromHours(7), [Tab("restored", "Fix the installer…"), Tab("blank")]);

        await _yard.InitializeAsync(CancellationToken.None);

        App.Chats.Select(c => (c.SessionId, c.Title, c.NotRunning, c.IsLive)).ShouldBe([("restored", "Fix the installer…", true, false), ("blank", "New chat", true, false)]);
        _yard.Tick(Now);
        App.Chats[0].ElapsedText.ShouldBe("", "nothing is known of when");
        App.NeedsAttention.ShouldBeFalse();
    }

    [Fact]
    public async Task A_tab_s_row_becomes_the_chat_s_once_it_runs_and_keeps_its_place()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        await VsCodeWrites(Tab("a", "Apple"), Tab("b", "Banana"));

        Says(Chat("a", SessionState.Working, title: null));

        App.Chats.Select(c => (c.SessionId, c.Title, c.NotRunning, c.State)).ShouldBe([("a", "Apple", false, SessionState.Working), ("b", "Banana", true, SessionState.Ended)]);

        Says(Chat("a", SessionState.Idle) with { Version = 2 });
        App.Chats[0].Title.ShouldBe("T a", "what the chat calls itself");
    }

    [Fact]
    public async Task An_idle_chat_stays_for_as_long_as_its_tab_is_open()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        await VsCodeWrites(Tab("a"));
        Says(Chat("a", SessionState.Idle));

        _time.Advance(TimeSpan.FromHours(30));
        Says(Chat("a", SessionState.Stale) with { LastEventAt = Now - TimeSpan.FromHours(30), Version = 2 });
        _yard.Tick(Now);

        Rows.ShouldBe(["a"]);
        App.Chats[0].NotRunning.ShouldBeFalse();
    }

    /// <summary>VS Code runs, the tab was just closed: the list written before still has it, and brings no row back.</summary>
    [Fact]
    public async Task A_chat_whose_tab_is_closed_leaves_at_once_though_VS_Code_s_list_still_has_it()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        App.HostState = HostState.Running;
        await VsCodeWrites(Tab("a"));
        Says(Chat("a", SessionState.Idle));

        _time.Advance(TimeSpan.FromMinutes(5));
        Says(Chat("a", SessionState.Ended) with { Version = 2 });

        Rows.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromSeconds(40));
        await VsCodeWrites();
        Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_closed_chat_is_kept_for_as_long_as_the_user_set_greyed_as_ended()
    {
        _yard.KeepClosed = TimeSpan.FromMinutes(5);
        await _yard.InitializeAsync(CancellationToken.None);
        App.HostState = HostState.Running;
        await VsCodeWrites();
        Says(Chat("a", SessionState.Ended));

        _time.Advance(TimeSpan.FromMinutes(4));
        _yard.Tick(Now);
        App.Chats.Select(c => (c.SessionId, c.NotRunning, c.IsLive)).ShouldBe([("a", false, false)]);

        _time.Advance(TimeSpan.FromMinutes(2));
        _yard.Tick(Now);
        Rows.ShouldBeEmpty();

        _yard.KeepClosed = TimeSpan.FromMinutes(10);
        Rows.ShouldBe(["a"], "the setting is followed as it changes");
    }

    /// <summary>VS Code closed, and with it every chat: the list it wrote as it closed is what it comes back with.</summary>
    [Fact]
    public async Task The_chats_of_a_VS_Code_that_closed_stay_as_tabs_that_do_not_run()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        App.HostState = HostState.Running;
        Says(Chat("a", SessionState.Idle));
        Says(Chat("b", SessionState.Idle));

        _time.Advance(TimeSpan.FromMinutes(5));
        await VsCodeWrites(Tab("a"), Tab("b"));
        _time.Advance(TimeSpan.FromSeconds(3)); // the ends are heard a moment after the list was written
        Says(Chat("a", SessionState.Ended) with { Version = 2 });
        Says(Chat("b", SessionState.Ended) with { Version = 2 });
        App.HostState = HostState.Stopped;

        App.Chats.Select(c => (c.SessionId, c.NotRunning)).ShouldBe([("a", true), ("b", true)]);

        _time.Advance(TimeSpan.FromHours(20));
        _yard.Tick(Now);
        Rows.ShouldBe(["a", "b"], "until the tabs are closed");
    }

    /// <summary>A VS Code that was killed wrote no last list: the one it has is what it comes back with all the same.</summary>
    [Fact]
    public async Task A_VS_Code_that_does_not_run_counts_its_last_list_however_old()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        await VsCodeWrites(Tab("a"));
        Says(Chat("a", SessionState.Idle));

        _time.Advance(TimeSpan.FromHours(2));
        Says(Chat("a", SessionState.Ended) with { Version = 2 });

        App.Chats.Select(c => (c.SessionId, c.NotRunning)).ShouldBe([("a", true)]);
    }

    [Fact]
    public async Task A_chat_that_runs_shows_before_VS_Code_wrote_its_tab_down_and_a_session_that_is_no_chat_does_not()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        await VsCodeWrites();

        Says(Chat("new", SessionState.Working));
        Says(Chat("window-load", SessionState.Idle, title: null));

        Rows.ShouldBe(["new"]);
    }

    /// <summary>A tab nothing was said in yet is a tab: it shows, where a session without a tab that said nothing does not.</summary>
    [Fact]
    public async Task A_new_chat_s_tab_shows_though_nothing_was_said_in_it()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        Says(Chat("blank", SessionState.Idle, title: null));
        Rows.ShouldBeEmpty();

        await VsCodeWrites(Tab("blank"));

        App.Chats.Select(c => (c.SessionId, c.Title, c.NotRunning)).ShouldBe([("blank", "New chat", false)]);
    }

    /// <summary>Without a tab, a chat quiet long enough to go stale has none any more: its end was not heard.</summary>
    [Fact]
    public async Task A_stale_chat_without_a_tab_is_closed()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        await VsCodeWrites();
        Says(Chat("a", SessionState.Idle));
        Rows.ShouldBe(["a"]);

        Says(Chat("a", SessionState.Stale) with { Version = 2 });

        Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_chat_idle_longer_than_the_user_set_is_hidden_tab_or_not_and_comes_back_when_it_works()
    {
        _yard.HideIdleAfter = TimeSpan.FromHours(4);
        _writtenIn["yesterday"] = Now - TimeSpan.FromHours(20);
        _writtenIn["this-morning"] = Now - TimeSpan.FromHours(2);
        await _yard.InitializeAsync(CancellationToken.None);
        await VsCodeWrites(Tab("a"), Tab("yesterday"), Tab("this-morning"), Tab("unknown"));
        Says(Chat("a", SessionState.Idle));
        Rows.ShouldBe(["a", "this-morning", "unknown"], ignoreOrder: true);

        _time.Advance(TimeSpan.FromHours(5));
        _yard.Tick(Now);
        Rows.ShouldBe(["unknown"], "a tab nothing is known of is not hidden");

        Says(Chat("a", SessionState.Working) with { LastEventAt = Now - TimeSpan.FromHours(5), Version = 2 });
        Rows.ShouldBe(["unknown", "a"]);

        _yard.HideIdleAfter = null;
        Rows.ShouldBe(["unknown", "a", "yesterday", "this-morning"], ignoreOrder: true);
    }

    /// <summary>A workspace VS Code never opened has no list: its chats show by what they report, as before #164.</summary>
    [Fact]
    public async Task Without_a_list_from_VS_Code_a_tile_goes_by_what_its_chats_report()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        Says(Chat("idle", SessionState.Idle));
        Says(Chat("ended", SessionState.Ended));

        Rows.ShouldBe(["idle"], "a closed chat is not kept unless the user says so");

        Says(Chat("idle", SessionState.Stale) with { Version = 2 });
        _time.Advance(WorkspaceTileViewModel.StaleRowLifetime - TimeSpan.FromMinutes(1));
        _yard.Tick(Now);
        Rows.ShouldBe(["idle"]);
        _time.Advance(TimeSpan.FromMinutes(2));
        _yard.Tick(Now);
        Rows.ShouldBeEmpty();
    }

    /// <summary>Raven closed the chat: the list VS Code has not written again still has its tab, which brings no row back.</summary>
    [Fact]
    public async Task A_chat_closed_on_purpose_is_not_brought_back_by_its_tab_until_it_runs_again()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        await VsCodeWrites(Tab("a"));
        Says(Chat("a", SessionState.Idle));

        _yard.ForgetChat("a");
        Says(Chat("a", SessionState.Ended) with { Version = 2 });
        await _yard.RefreshTabsAsync();
        Rows.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromMinutes(1));
        Says(Chat("a", SessionState.Working) with { LastEventAt = Now, Version = 3 });
        Rows.ShouldBe(["a"]);
    }

    /// <summary>A multi-root window: the chat runs in a folder that is another tile's, and shows there only.</summary>
    [Fact]
    public async Task A_tab_of_a_chat_another_tile_shows_is_not_shown_twice()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        Says(Chat("a", SessionState.Idle, workspace: _shop.Id));

        await VsCodeWrites(Tab("a"));

        Rows.ShouldBeEmpty();
        _yard.FindTile(_shop.Id)!.Chats.Select(c => c.SessionId).ShouldBe(["a"]);
    }

    [Fact]
    public async Task The_tabs_are_looked_at_again_every_few_seconds()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        Rows.ShouldBeEmpty();

        _tabs.Of[_app.Id] = new OpenChatTabs(Now, [Tab("a")]);
        _time.Advance(YardViewModel.TabsInterval);
        for (var i = 0; i < 200 && Rows.Length == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Rows.ShouldBe(["a"]);
    }

    private sealed class FakeTabs : IVsCodeOpenTabs
    {
        public Dictionary<Guid, OpenChatTabs> Of { get; } = [];

        public IReadOnlyDictionary<Guid, OpenChatTabs> Read(IReadOnlyList<Workspace> workspaces) =>
            new Dictionary<Guid, OpenChatTabs>(Of.Where(t => workspaces.Any(w => w.Id == t.Key)));
    }
}
