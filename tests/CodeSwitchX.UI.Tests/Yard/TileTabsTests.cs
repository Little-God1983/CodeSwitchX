using System.Collections.Concurrent;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.Raven;
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
    // Read by the looks on the thread pool, as the fake's list is.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _writtenIn = new();
    private readonly ConcurrentDictionary<string, string> _titles = new();
    private readonly ConcurrentDictionary<string, bool> _running = new(StringComparer.OrdinalIgnoreCase);
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
            _bus, new ImmediateDispatcher(), _time, NullLogger<YardViewModel>.Instance, _tabs,
            id => _writtenIn.TryGetValue(id, out var at) ? new TabConversation(at, _titles.GetValueOrDefault(id)) : null, () => _running);
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private WorkspaceTileViewModel App => _yard.FindTile(_app.Id)!;

    private string[] Rows => [.. App.Chats.Select(c => c.SessionId)];

    private SessionSnapshot Chat(string id, SessionState state, string? title = "T", Guid? workspace = null) => new()
    {
        SessionId = id, WorkspaceId = workspace ?? _app.Id, State = state, StartedAt = Now, LastEventAt = Now, StateSince = Now, Title = title is null ? null : $"{title} {id}",
    };

    private void Says(SessionSnapshot chat) => _bus.Publish(new SessionChanged(null, chat));

    /// <summary>VS Code writes the tabs of the App window down, now, and the tiles look.</summary>
    private Task VsCodeWrites(params OpenChatTab[] tabs)
    {
        _tabs.Of[_app.Id] = new OpenChatTabs(Now, tabs);
        return _yard.RefreshTabsAsync();
    }

    private static OpenChatTab Tab(string id, string? title = null) => new(id, title);

    /// <summary>Time passes; the look at the tabs it sets off is over before the test goes on.</summary>
    private async Task Pass(TimeSpan time)
    {
        _time.Advance(time);
        await _yard.CurrentTabsRefresh;
    }

    [Fact]
    public async Task A_tab_VS_Code_will_come_back_with_shows_before_its_chat_ever_ran()
    {
        _tabs.Of[_app.Id] = new OpenChatTabs(Now - TimeSpan.FromHours(7), [Tab("restored", "Fix the installer…"), Tab("blank")]);

        await _yard.InitializeAsync(CancellationToken.None);

        App.Chats.Select(c => (c.SessionId, c.Title, c.NotRunning, c.IsLive)).ShouldBe([("restored", "Fix the installer…", true, false), ("blank", "New chat", true, false)]);
        _yard.Tick(Now);
        App.Chats[0].ElapsedText.ShouldBe("", "nothing is known of when");
        App.NeedsAttention.ShouldBeFalse();
        App.IsWorking.ShouldBeFalse("a tab alone works at nothing (#182)");
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

        await Pass(TimeSpan.FromHours(30));
        Says(Chat("a", SessionState.Stale) with { LastEventAt = Now - TimeSpan.FromHours(30), Version = 2 });
        _yard.Tick(Now);

        Rows.ShouldBe(["a"]);
        App.Chats[0].NotRunning.ShouldBeFalse();
    }

    /// <summary>
    /// VS Code runs, the tab was just closed: VS Code, hidden in the Cab, does not write its list again for minutes, and the
    /// list it wrote just as the tab closed still has it (seen on screen). It brings no row back.
    /// </summary>
    [Fact]
    public async Task A_chat_whose_tab_is_closed_leaves_at_once_though_VS_Code_s_list_still_has_it()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        App.HostState = HostState.Running;
        await VsCodeWrites(Tab("a"));
        Says(Chat("a", SessionState.Idle));

        await Pass(TimeSpan.FromMinutes(5));
        Says(Chat("a", SessionState.Ended) with { Version = 2 });
        await VsCodeWrites(Tab("a"));

        Rows.ShouldBeEmpty();

        await Pass(TimeSpan.FromMinutes(4));
        _yard.Tick(Now);
        Rows.ShouldBeEmpty();
    }

    /// <summary>
    /// Reload Window: every chat ends while VS Code runs, and VS Code, reloaded, writes its tabs down again with them. Those
    /// are open; a list written as the tab closed (within <see cref="WorkspaceTileViewModel.ListedAfterEnd"/>) is not.
    /// </summary>
    [Fact]
    public async Task Tabs_VS_Code_writes_down_again_well_after_their_chats_ended_are_open()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        App.HostState = HostState.Running;
        await VsCodeWrites(Tab("a"), Tab("b"));
        Says(Chat("a", SessionState.Idle));
        Says(Chat("b", SessionState.Idle));

        await Pass(TimeSpan.FromMinutes(5));
        Says(Chat("a", SessionState.Ended) with { Version = 2 });
        Says(Chat("b", SessionState.Ended) with { Version = 2 });
        await Pass(TimeSpan.FromSeconds(1));
        await VsCodeWrites(Tab("a"), Tab("b"));
        Rows.ShouldBeEmpty("written as they ended");

        await Pass(TimeSpan.FromSeconds(6));
        await VsCodeWrites(Tab("a"), Tab("b"));
        App.Chats.Select(c => (c.SessionId, c.NotRunning)).ShouldBe([("a", true), ("b", true)]);
    }

    /// <summary>VS Code closes: the tile waits for the tabs it wrote as it closed, and a tab closed earlier does not flash back.</summary>
    [Fact]
    public async Task A_VS_Code_that_closes_shows_the_tabs_it_wrote_as_it_closed_not_the_ones_read_before()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        App.HostState = HostState.Running;
        await VsCodeWrites(Tab("a"), Tab("b"));
        Says(Chat("a", SessionState.Idle));
        Says(Chat("b", SessionState.Idle));
        await Pass(TimeSpan.FromMinutes(1));
        Says(Chat("a", SessionState.Ended) with { Version = 2 }); // its tab closed; the list read still has it

        await Pass(TimeSpan.FromMinutes(1));
        Says(Chat("b", SessionState.Ended) with { Version = 2 });
        _tabs.Of[_app.Id] = new OpenChatTabs(Now, [Tab("b")]); // what VS Code wrote as it closed
        var seen = new List<string[]>();
        App.Chats.CollectionChanged += (_, _) => seen.Add(Rows);
        App.HostState = HostState.Stopped;
        await _yard.CurrentTabsRefresh;

        Rows.ShouldBe(["b"]);
        seen.ShouldAllBe(rows => !rows.Contains("a"), "the tab closed earlier never shows again");
    }

    /// <summary>VS Code started with the tabs it had: their chats ended before it ran, and show as tabs that do not run.</summary>
    [Fact]
    public async Task The_tabs_VS_Code_brings_back_stay_as_tabs_that_do_not_run_once_it_runs()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        await VsCodeWrites(Tab("a"));
        Says(Chat("a", SessionState.Ended));

        await Pass(TimeSpan.FromMinutes(1));
        App.HostState = HostState.Running;

        App.Chats.Select(c => (c.SessionId, c.NotRunning)).ShouldBe([("a", true)]);
    }

    [Fact]
    public async Task A_closed_chat_is_kept_for_as_long_as_the_user_set_greyed_as_ended()
    {
        _yard.KeepClosed = TimeSpan.FromMinutes(5);
        await _yard.InitializeAsync(CancellationToken.None);
        App.HostState = HostState.Running;
        await VsCodeWrites(Tab("a", "Closed tab's title"));
        Says(Chat("a", SessionState.Ended, title: null) with { LastToolName = "Bash" }); // a chat that never got a title
        App.Chats.ShouldHaveSingleItem().Title.ShouldNotBe("Closed tab's title", "the tab still listed was closed");

        await Pass(TimeSpan.FromMinutes(4));
        _yard.Tick(Now);
        App.Chats.Select(c => (c.SessionId, c.NotRunning, c.IsLive)).ShouldBe([("a", false, false)]);

        await Pass(TimeSpan.FromMinutes(2));
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

        // The chats' ends are heard before the window is seen gone, and the list VS Code wrote as it closed comes last.
        await Pass(TimeSpan.FromMinutes(5));
        Says(Chat("a", SessionState.Ended) with { Version = 2 });
        Says(Chat("b", SessionState.Ended) with { Version = 2 });
        App.HostState = HostState.Stopped;
        await VsCodeWrites(Tab("a"), Tab("b"));

        App.Chats.Select(c => (c.SessionId, c.NotRunning)).ShouldBe([("a", true), ("b", true)]);

        await Pass(TimeSpan.FromHours(20));
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

        await Pass(TimeSpan.FromHours(2));
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

    /// <summary>A chat in VS Code's side bar, or a terminal, has no tab: it shows while it reports, and as long as before once it went stale.</summary>
    [Fact]
    public async Task A_chat_without_a_tab_shows_while_it_reports_and_a_while_after_it_went_stale()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        await VsCodeWrites();
        Says(Chat("a", SessionState.Idle));
        Rows.ShouldBe(["a"]);

        Says(Chat("a", SessionState.Stale) with { Version = 2 });
        await Pass(WorkspaceTileViewModel.StaleRowLifetime - TimeSpan.FromMinutes(1));
        _yard.Tick(Now);
        Rows.ShouldBe(["a"]);

        await Pass(TimeSpan.FromMinutes(2));
        _yard.Tick(Now);
        Rows.ShouldBeEmpty();
    }

    /// <summary>Its Claude Code crashed: nothing said the tab closed, and VS Code, which runs, has not written its list since.</summary>
    [Fact]
    public async Task A_chat_whose_Claude_Code_went_without_a_word_keeps_its_row_while_its_tab_is_listed()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        App.HostState = HostState.Running;
        await VsCodeWrites(Tab("a"));
        Says(Chat("a", SessionState.Working));

        await Pass(TimeSpan.FromMinutes(5));
        Says(Chat("a", SessionState.Errored) with { Version = 2 });

        App.Chats.Select(c => (c.SessionId, c.State, c.NotRunning)).ShouldBe([("a", SessionState.Errored, false)], "its red dot says what happened");
    }

    /// <summary>Hidden for being idle, and ended long ago: its tab does not bring it back as a tab nothing is known of.</summary>
    [Fact]
    public async Task An_ended_chat_hidden_for_being_idle_stays_hidden_while_its_tab_is_listed()
    {
        _yard.HideIdleAfter = TimeSpan.FromHours(1);
        await _yard.InitializeAsync(CancellationToken.None);
        await VsCodeWrites(Tab("a"));
        Says(Chat("a", SessionState.Ended));
        Rows.ShouldBe(["a"]);

        await Pass(TimeSpan.FromHours(3));
        _yard.Tick(Now);
        _yard.Tick(Now);
        await _yard.RefreshTabsAsync();

        Rows.ShouldBeEmpty();
    }

    /// <summary>The folder cannot be listed for a moment: the tiles keep their tabs, and a chat closed on purpose stays gone.</summary>
    [Fact]
    public async Task A_look_that_gives_no_list_keeps_a_closed_chat_gone()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        await VsCodeWrites(Tab("a"));
        Says(Chat("a", SessionState.Idle));
        _yard.ForgetChat("a");
        Says(Chat("a", SessionState.Ended) with { Version = 2 });

        var list = _tabs.Of[_app.Id];
        _tabs.Of.Clear();
        await _yard.RefreshTabsAsync();
        _tabs.Of[_app.Id] = list;
        await _yard.RefreshTabsAsync();

        Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_tab_nothing_is_known_of_tells_Raven_when_it_was_listed_and_shows_a_time_only_when_it_was_written_in()
    {
        _writtenIn["known"] = Now - TimeSpan.FromHours(2);
        await _yard.InitializeAsync(CancellationToken.None);
        await VsCodeWrites(Tab("known"), Tab("unknown"));
        _yard.Tick(Now);

        App.Chats.Select(c => (c.SessionId, c.StateSince, c.ElapsedText)).ShouldBe([("known", Now - TimeSpan.FromHours(2), "2h 00m"), ("unknown", Now, "")]);
    }

    /// <summary>
    /// The app was not running when the chat started: nothing was heard of it. Its Claude Code runs in its tab, so it is
    /// idle, not ended; and its conversation has the whole title where the tab's is cut short.
    /// </summary>
    [Fact]
    public async Task A_tab_nothing_was_heard_of_is_idle_while_its_Claude_Code_runs_and_has_its_conversation_s_title()
    {
        _writtenIn["runs"] = _writtenIn["asleep"] = Now - TimeSpan.FromHours(4);
        _titles["runs"] = "Text-to-speech setup dialog with voice selector";
        _running["RUNS"] = false;
        _running["waits"] = true;
        _writtenIn["waits"] = Now - TimeSpan.FromMinutes(3);
        await _yard.InitializeAsync(CancellationToken.None);

        await VsCodeWrites(Tab("runs", "Text-to-speech setup dia…"), Tab("asleep", "DiffusionNexus.Installer…"), Tab("waits", "Permission"));

        App.Chats.Select(c => (c.Title, c.State, c.NotRunning)).ShouldBe(
        [
            ("Text-to-speech setup dialog with voice selector", SessionState.Idle, false),
            ("DiffusionNexus.Installer…", SessionState.Ended, true),
            ("Permission", SessionState.Waiting, false),
        ]);
        App.NeedsAttention.ShouldBeTrue("its tab waits on the user");
    }

    /// <summary>A title given or made later is shown a minute on; a hook that brings no title yet does not take it away.</summary>
    [Fact]
    public async Task A_tab_s_title_follows_its_conversation_and_stays_when_its_chat_reports_without_one()
    {
        _writtenIn["a"] = Now;
        _titles["a"] = "First guess";
        await _yard.InitializeAsync(CancellationToken.None);
        await VsCodeWrites(Tab("a", "First gu…"));
        App.Chats[0].Title.ShouldBe("First guess");

        _titles["a"] = "Voice setup";
        await Pass(YardViewModel.AskAgainAfter);
        await _yard.RefreshTabsAsync();
        App.Chats[0].Title.ShouldBe("Voice setup");

        Says(Chat("a", SessionState.Working, title: null));
        App.Chats[0].Title.ShouldBe("Voice setup");
    }

    [Fact]
    public async Task A_row_made_for_a_chat_Raven_started_has_its_voice_mark_whenever_it_is_made()
    {
        _yard.HideIdleAfter = TimeSpan.FromHours(1);
        await _yard.InitializeAsync(CancellationToken.None);
        await VsCodeWrites(Tab("a"));
        Says(Chat("a", SessionState.Idle));
        _yard.MarkVoice("a", "Fable 5.1 · high");
        await Pass(TimeSpan.FromHours(2));
        _yard.Tick(Now);
        Rows.ShouldBeEmpty();

        _yard.HideIdleAfter = null;

        App.Chats.ShouldHaveSingleItem().VoiceLabel.ShouldBe("Fable 5.1 · high");
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

        await Pass(TimeSpan.FromHours(5));
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

        await Pass(TimeSpan.FromMinutes(1));
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

        // Ended, it is still that tile's chat: the tab here is no row of its own.
        Says(Chat("a", SessionState.Ended, workspace: _shop.Id) with { Version = 2 });
        await _yard.RefreshTabsAsync();
        Rows.ShouldBeEmpty();
    }

    /// <summary>The chat is another tile's now: this one keeps no row that shows its last state for good.</summary>
    [Fact]
    public async Task A_chat_that_moves_to_another_tile_leaves_no_row_behind_though_its_tab_is_listed_here()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        await VsCodeWrites(Tab("a"));
        Says(Chat("a", SessionState.Waiting));
        App.NeedsAttention.ShouldBeTrue();

        Says(Chat("a", SessionState.Waiting, workspace: _shop.Id) with { Version = 2 });

        Rows.ShouldBeEmpty();
        App.NeedsAttention.ShouldBeFalse();
        _yard.FindTile(_shop.Id)!.Chats.Select(c => c.SessionId).ShouldBe(["a"]);
    }

    /// <summary>A new tab has no conversation yet: when it was written in is asked for again, not taken as never known.</summary>
    [Fact]
    public async Task When_a_tab_was_written_in_is_asked_for_again_until_it_is_known()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        await VsCodeWrites(Tab("a"));
        _yard.Tick(Now);
        App.Chats[0].ElapsedText.ShouldBe("");

        _writtenIn["a"] = Now;
        await _yard.RefreshTabsAsync();
        _yard.Tick(Now);
        App.Chats[0].ElapsedText.ShouldBe("", "not looked for on every look: the folders of every project are gone through for it");

        await Pass(YardViewModel.AskAgainAfter);
        await _yard.RefreshTabsAsync();
        _yard.Tick(Now);

        App.Chats[0].ElapsedText.ShouldBe("1m");
    }

    [Fact]
    public async Task A_tile_added_while_the_app_runs_shows_its_tabs_at_once()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        var extra = new Workspace { Name = "Extra", RootPath = @"c:\repo\extra", TrackId = _general.Id };
        _tabs.Of[extra.Id] = new OpenChatTabs(Now, [Tab("x", "Restored")]);

        _bus.Publish(new WorkspaceRegistered(extra));
        await _yard.CurrentTabsRefresh;

        _yard.FindTile(extra.Id)!.Chats.Select(c => c.Title).ShouldBe(["Restored"]);
    }

    [Fact]
    public async Task The_tabs_are_looked_at_again_every_few_seconds()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        Rows.ShouldBeEmpty();

        _tabs.Of[_app.Id] = new OpenChatTabs(Now, [Tab("a")]);
        await Pass(YardViewModel.TabsInterval);
        for (var i = 0; i < 200 && Rows.Length == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Rows.ShouldBe(["a"]);
    }

    /// <summary>
    /// A stop sets off a look; VS Code writes its list as it closes, after the look took the list, and the tile looks again
    /// (#238). That look is not the one running, which has the list from before: it reads again after it.
    /// </summary>
    [Fact]
    public async Task A_look_asked_for_while_one_runs_reads_the_list_again_after_it()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        var before = _tabs.Reads; // the start's own look
        _tabs.Hold = new ManualResetEventSlim(); // not disposed: a late read may still wait on it
        var running = _yard.RefreshTabsAsync();
        for (var i = 0; i < 500 && _tabs.Reads == before; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Task look;
        try
        {
            _tabs.Reads.ShouldBe(before + 1, "the running look took the list before VS Code wrote it");
            look = VsCodeWrites(Tab("a"));
        }
        finally
        {
            _tabs.Hold.Set(); // a failure must not leave a pool thread held
        }

        await running;
        await look;

        Rows.ShouldBe(["a"]);
        _tabs.Reads.ShouldBe(before + 2);
    }

    private sealed class FakeTabs : IVsCodeOpenTabs
    {
        // Written by the test while a look reads it on the thread pool.
        public ConcurrentDictionary<Guid, OpenChatTabs> Of { get; } = new();

        /// <summary>Holds each read once it took the list, until set.</summary>
        public ManualResetEventSlim? Hold { get; set; }

        private int _reads;

        /// <summary>How many reads took the list.</summary>
        public int Reads => Volatile.Read(ref _reads);

        public IReadOnlyDictionary<Guid, OpenChatTabs> Read(IReadOnlyList<Workspace> workspaces)
        {
            var read = new Dictionary<Guid, OpenChatTabs>(Of.Where(t => workspaces.Any(w => w.Id == t.Key)));
            Interlocked.Increment(ref _reads);
            Hold?.Wait(TimeSpan.FromSeconds(10));
            return read;
        }
    }
}
