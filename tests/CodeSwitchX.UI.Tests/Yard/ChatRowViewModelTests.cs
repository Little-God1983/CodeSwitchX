using System.Globalization;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.UI.Yard;

namespace CodeSwitchX.UI.Tests.Yard;

public class ChatRowViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly PricingTable Pricing = new([new PricingRule { Model = "claude-sonnet-5", ContextWindow = 1_000_000 }]);

    [Theory]
    [InlineData(5, "5s")]
    [InlineData(65, "1m")]
    [InlineData(3_700, "1h 01m")]
    [InlineData(90_000, "1d 1h")]
    public void Elapsed_is_compact(int seconds, string expected)
    {
        ChatRowViewModel.FormatElapsed(TimeSpan.FromSeconds(seconds)).ShouldBe(expected);
    }

    [Fact]
    public void Update_copies_the_snapshot_and_computes_context_pressure()
    {
        var row = new ChatRowViewModel("s1");
        row.Update(new SessionSnapshot
        {
            SessionId = "s1", Title = "Fix build", State = SessionState.Waiting, StartedAt = Now.AddMinutes(-10), LastEventAt = Now, StateSince = Now.AddSeconds(-30),
            Model = "claude-sonnet-5", Inferred = true, LastToolName = "Bash", LatestContext = new TokenUsage(100_000, 0, 50_000, 700_000, 0),
        }, Pricing);
        row.Tick(Now);

        row.Title.ShouldBe("Fix build");
        row.State.ShouldBe(SessionState.Waiting);
        row.ElapsedText.ShouldBe("30s");
        row.ContextFill.ShouldBe(0.85, tolerance: 1e-9);
        row.Pressure.ShouldBe(ContextPressure.Amber);
        row.Inferred.ShouldBeTrue();
        row.LastToolName.ShouldBe("Bash");
        row.IsLive.ShouldBeTrue();
    }

    [Fact]
    public void A_chat_idle_through_a_keep_alive_turn_shows_its_time_since_the_user_last_used_it()
    {
        var row = new ChatRowViewModel("s1");
        var used = Now.AddMinutes(-57);

        row.Update(new SessionSnapshot
        {
            SessionId = "s1", Title = "Fix build", State = SessionState.Idle, StartedAt = Now.AddHours(-2), LastEventAt = Now, StateSince = Now.AddSeconds(-20),
            LastUseAt = used, KeepAliveTurn = true,
        }, Pricing);
        row.Tick(Now);

        row.ElapsedText.ShouldBe("57m", "the ping's turn is not the user coming back");
    }

    /// <summary>#386: the ping that keeps its cache warm is no turn of the user's, so the row keeps its place on the tile.</summary>
    [Fact]
    public void A_keep_alive_turn_does_not_move_the_row_up()
    {
        var row = new ChatRowViewModel("s1");
        var used = Now.AddMinutes(-55);

        row.Update(new SessionSnapshot
        {
            SessionId = "s1", Title = "Fix build", State = SessionState.Working, StartedAt = Now.AddHours(-2), LastEventAt = Now, StateSince = Now,
            LastUseAt = used, KeepAliveTurn = true, TurnStartedAt = Now,
        }, Pricing);

        row.LastTurnAt.ShouldBe(used);
    }

    [Fact]
    public void A_chat_working_on_a_job_through_a_keep_alive_turn_still_counts_from_when_it_began_working()
    {
        var row = new ChatRowViewModel("s1");
        var began = Now.AddMinutes(-60);

        row.Update(new SessionSnapshot
        {
            SessionId = "s1", Title = "Build", State = SessionState.Working, StartedAt = Now.AddHours(-2), LastEventAt = Now, StateSince = began,
            LastUseAt = Now.AddMinutes(-45), KeepAliveTurn = true, TurnStartedAt = Now, OpenJobs = ["job1"],
        }, Pricing);

        row.LastTurnAt.ShouldBe(began);
    }

    private static SessionSnapshot Idle(DateTimeOffset? lastRequest = null) => new()
    {
        SessionId = "s1", Title = "Fix build", State = SessionState.Idle, StartedAt = Now.AddHours(-2), LastEventAt = Now, StateSince = Now.AddMinutes(-5),
        LastRequestAt = lastRequest,
    };

    private static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>#386: a row with no menu behind it (a test's, a preview's) offers none.</summary>
    [Fact]
    public void A_row_without_a_menu_offers_none()
    {
        var row = new ChatRowViewModel("s1");
        row.Update(Idle(), Pricing);

        row.HasMenu.ShouldBeFalse();
        row.CompactNowCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void A_chat_kept_warm_says_until_when_on_its_gauge_and_in_its_menu()
    {
        var menu = new FakeRowMenu { Until = Now.AddHours(4) };
        var row = new ChatRowViewModel("s1", menu: menu);
        row.Update(Idle(Now.AddMinutes(-10)), Pricing);

        row.Tick(Now);

        row.IsKeptWarm.ShouldBeTrue();
        row.KeepWarmHeader.ShouldBe($"Keep warm (until {Clock(Now.AddHours(4))})");
        row.KeptWarmTip.ShouldBe($"Kept warm until {Clock(Now.AddHours(4))}: a keep-alive message is sent to it before its cache runs out");
        row.CacheTip.ShouldBe($"Cache hot: 50 minutes left{Environment.NewLine}Kept warm until {Clock(Now.AddHours(4))}");
    }

    [Fact]
    public void A_chat_not_kept_warm_shows_no_mark()
    {
        var row = new ChatRowViewModel("s1", menu: new FakeRowMenu());
        row.Update(Idle(Now.AddMinutes(-10)), Pricing);

        row.Tick(Now);

        row.HasMenu.ShouldBeTrue();
        row.IsKeptWarm.ShouldBeFalse();
        row.KeepWarmHeader.ShouldBe("Keep warm");
        row.KeptWarmTip.ShouldBeNull();
        row.CacheTip.ShouldBe("Cache hot: 50 minutes left");
    }

    [Fact]
    public void The_mark_goes_when_keeping_warm_ends()
    {
        var menu = new FakeRowMenu { Until = Now.AddHours(4) };
        var row = new ChatRowViewModel("s1", menu: menu);
        row.Update(Idle(Now.AddMinutes(-10)), Pricing);
        row.Tick(Now);

        menu.Until = null;
        row.Tick(Now.AddSeconds(1));

        row.IsKeptWarm.ShouldBeFalse();
    }

    [Theory]
    [InlineData("2", 2)]
    [InlineData("4", 4)]
    [InlineData("8", 8)]
    public void Keep_warm_is_asked_for_the_hours_picked(string picked, int hours)
    {
        var menu = new FakeRowMenu();
        var row = new ChatRowViewModel("s1", menu: menu);

        row.KeepWarmCommand.Execute(picked);

        menu.Kept.ShouldBe([("s1", TimeSpan.FromHours(hours))]);
    }

    [Fact]
    public void Stop_keeping_warm_stops_it()
    {
        var menu = new FakeRowMenu { Until = Now.AddHours(4) };
        var row = new ChatRowViewModel("s1", menu: menu);

        row.StopKeepingWarmCommand.Execute(null);

        menu.Stopped.ShouldBe(["s1"]);
    }

    [Fact]
    public void An_opening_menu_asks_whether_the_chat_can_be_kept_warm()
    {
        var menu = new FakeRowMenu { WhyNot = "Its cache is cold already." };
        var row = new ChatRowViewModel("s1", menu: menu);
        row.Update(Idle(Now.AddMinutes(-61)), Pricing);

        row.MenuOpening();

        row.CanKeepWarm.ShouldBeFalse();
        row.KeepWarmBlock.ShouldBe("Its cache is cold already.");

        menu.WhyNot = null;
        row.MenuOpening();

        row.CanKeepWarm.ShouldBeTrue();
        row.KeepWarmBlock.ShouldBeNull();
    }

    [Fact]
    public void Compact_now_compacts_the_chat()
    {
        var menu = new FakeRowMenu();
        var row = new ChatRowViewModel("s1", menu: menu);
        row.Update(Idle(), Pricing);

        row.CompactNowCommand.CanExecute(null).ShouldBeTrue();
        row.CompactNowCommand.Execute(null);

        menu.Compacted.ShouldBe(["s1"]);
    }

    [Fact]
    public void A_chat_being_compacted_says_so_in_place_of_its_time_and_is_not_compacted_twice()
    {
        var menu = new FakeRowMenu { Compacting = true };
        var row = new ChatRowViewModel("s1", menu: menu);
        row.Update(Idle(), Pricing);

        row.Tick(Now);

        row.ElapsedText.ShouldBe("compacting…");
        row.CompactNowCommand.CanExecute(null).ShouldBeFalse();

        menu.Compacting = false;
        row.Tick(Now);

        row.ElapsedText.ShouldBe("5m");
        row.CompactNowCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public void A_tab_nothing_was_said_in_has_nothing_to_compact()
    {
        var row = new ChatRowViewModel("s1", menu: new FakeRowMenu());
        row.ShowTab(new OpenChatTab("s1", "Claude Code"), notRunning: false);

        row.CompactNowCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void A_tab_the_app_knows_no_chat_of_is_not_compacted_from_here()
    {
        var row = new ChatRowViewModel("s1", menu: new FakeRowMenu());
        row.ShowTab(new OpenChatTab("s1", "Claude Code"), notRunning: true, new TabConversation(Now.AddHours(-3), "Fix build"));

        row.CompactNowCommand.CanExecute(null).ShouldBeFalse("the app does not know the folder it runs in, which Claude Code finds its conversation by");
    }

    [Fact]
    public void A_session_nothing_was_said_in_has_nothing_to_compact()
    {
        var row = new ChatRowViewModel("s1", menu: new FakeRowMenu());
        row.Update(Idle() with { Title = null }, Pricing);

        row.CompactNowCommand.CanExecute(null).ShouldBeFalse();
    }

    private sealed class FakeRowMenu : IChatRowMenu
    {
        public DateTimeOffset? Until { get; set; }
        public string? WhyNot { get; set; }
        public bool Compacting { get; set; }
        public List<string> Compacted { get; } = [];
        public List<(string Id, TimeSpan For)> Kept { get; } = [];
        public List<string> Stopped { get; } = [];

        public Task CompactAsync(string sessionId)
        {
            Compacted.Add(sessionId);
            return Task.CompletedTask;
        }

        public bool IsCompacting(string sessionId) => Compacting;
        public DateTimeOffset? KeptWarmUntil(string sessionId) => Until;
        public string? WhyNotKeptWarm(string sessionId) => WhyNot;
        public void KeepWarm(string sessionId, TimeSpan duration) => Kept.Add((sessionId, duration));
        public void StopKeepingWarm(string sessionId) => Stopped.Add(sessionId);
    }

    [Fact]
    public void A_chat_on_the_1M_context_option_measures_its_fill_against_a_million()
    {
        var row = new ChatRowViewModel("s1");
        var pricing = new PricingTable([new PricingRule { Model = "claude-sonnet-4-5", ContextWindow = 200_000 }]);

        row.Update(new SessionSnapshot
        {
            SessionId = "s1", State = SessionState.Working, StartedAt = Now, LastEventAt = Now, StateSince = Now,
            Model = "claude-sonnet-4-5[1m]", LatestContext = new TokenUsage(190_000, 0, 0, 0, 0),
        }, pricing);

        row.ContextFill.ShouldBe(0.19, tolerance: 1e-9);
        row.Pressure.ShouldBe(ContextPressure.Normal);
    }

    [Fact]
    public void A_chat_on_a_model_without_a_rule_shows_no_context_fill()
    {
        var row = new ChatRowViewModel("s1");

        row.Update(new SessionSnapshot
        {
            SessionId = "s1", State = SessionState.Working, StartedAt = Now, LastEventAt = Now, StateSince = Now,
            Model = "claude-mythos-6", LatestContext = new TokenUsage(180_000, 0, 0, 0, 0),
        }, PricingTable.Default);

        row.ContextFill.ShouldBe(0.0, "the window of a model without a rule is not known");
        row.Pressure.ShouldBe(ContextPressure.Normal);
    }

    private static SessionSnapshot Requested(DateTimeOffset? at) => new()
    {
        SessionId = "s1", State = SessionState.Idle, StartedAt = Now.AddHours(-3), LastEventAt = Now, StateSince = Now, LastRequestAt = at,
    };

    [Theory]
    [InlineData(720, CacheWarmth.Hot, "Cache hot: 48 minutes left")]
    [InlineData(2_640, CacheWarmth.Warm, "Cache warm: 16 minutes left")]
    [InlineData(3_540, CacheWarmth.Warm, "Cache warm: 1 minute left")]
    [InlineData(3_570, CacheWarmth.Warm, "Cache warm: less than a minute left")]
    [InlineData(4_200, CacheWarmth.Cold, "Cache cold: its hour ran out")]
    public void The_gauge_shows_how_warm_the_cache_is(int secondsAgo, CacheWarmth warmth, string tip)
    {
        var row = new ChatRowViewModel("s1");
        row.Update(Requested(Now.AddSeconds(-secondsAgo)), Pricing);

        row.Tick(Now);

        row.CacheWarmth.ShouldBe(warmth);
        row.CacheTip.ShouldBe(tip);
    }

    [Fact]
    public void The_gauge_drains_as_time_goes_by_and_fills_again_with_a_new_request()
    {
        var row = new ChatRowViewModel("s1");
        row.Update(Requested(Now), Pricing);
        row.Tick(Now);
        row.CacheFill.ShouldBe(1.0);

        row.Tick(Now.AddMinutes(44));
        row.CacheFill.ShouldBe(16.0 / 60, tolerance: 1e-9);
        row.CacheWarmth.ShouldBe(CacheWarmth.Warm);

        row.Update(Requested(Now.AddMinutes(44)), Pricing);
        row.Tick(Now.AddMinutes(44));
        row.CacheFill.ShouldBe(1.0);
        row.CacheWarmth.ShouldBe(CacheWarmth.Hot);
    }

    [Fact]
    public void A_chat_that_made_no_request_shows_no_temperature()
    {
        var row = new ChatRowViewModel("s1");
        row.Update(Requested(null), Pricing);

        row.Tick(Now);

        row.CacheWarmth.ShouldBe(CacheWarmth.Unknown);
        row.CacheTip.ShouldBeNull();
    }

    [Fact]
    public void A_tab_alone_whose_conversation_was_written_over_an_hour_ago_is_cold()
    {
        var row = new ChatRowViewModel("s1");

        row.ShowTab(new OpenChatTab("s1", "Old chat"), notRunning: true, new TabConversation(Now.AddHours(-2), null), Now);
        row.Tick(Now);

        row.CacheWarmth.ShouldBe(CacheWarmth.Cold, "no request can be later than the last write");
        row.CacheFill.ShouldBe(1.0);
        row.CacheTip.ShouldBe("Cache cold: its hour ran out");
    }

    [Fact]
    public void A_tab_alone_written_in_within_the_hour_tells_no_temperature()
    {
        var row = new ChatRowViewModel("s1");

        row.ShowTab(new OpenChatTab("s1", "Recent chat"), notRunning: true, new TabConversation(Now.AddMinutes(-10), null), Now);
        row.Tick(Now);

        row.CacheWarmth.ShouldBe(CacheWarmth.Unknown, "a write need not be a request, so the time left is not known");

        row.Tick(Now.AddMinutes(50));

        row.CacheWarmth.ShouldBe(CacheWarmth.Cold);
    }

    [Fact]
    public void A_cold_tab_stays_cold_when_its_chat_starts_until_it_makes_a_request()
    {
        var row = new ChatRowViewModel("s1");
        row.ShowTab(new OpenChatTab("s1", "Old chat"), notRunning: true, new TabConversation(Now.AddHours(-2), null), Now);

        row.Update(Requested(null), Pricing);
        row.Tick(Now);
        row.CacheWarmth.ShouldBe(CacheWarmth.Cold, "starting a chat asks nothing of its cache");

        row.Update(Requested(Now), Pricing);
        row.Tick(Now);
        row.CacheWarmth.ShouldBe(CacheWarmth.Hot);
    }

    [Fact]
    public void Each_temperature_has_a_colour_of_its_own()
    {
        var converter = new CacheWarmthToBrushConverter();
        var brushes = new[] { CacheWarmth.Hot, CacheWarmth.Warm, CacheWarmth.Cold }.Select(w => converter.Convert(w, typeof(object), null, CultureInfo.InvariantCulture)).ToList();

        brushes.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public void Untitled_sessions_show_a_placeholder()
    {
        var row = new ChatRowViewModel("abcdef12-3456");
        row.Update(new SessionSnapshot { SessionId = "abcdef12-3456", State = SessionState.Idle, StartedAt = Now, LastEventAt = Now, StateSince = Now }, Pricing);

        row.Title.ShouldBe("Chat abcdef12");
    }

    [Fact]
    public void Update_ignores_snapshots_older_than_the_one_already_shown()
    {
        var row = new ChatRowViewModel("s1");
        var newer = new SessionSnapshot { SessionId = "s1", State = SessionState.Working, StartedAt = Now, LastEventAt = Now, StateSince = Now, Version = 7 };
        var older = newer with { State = SessionState.Idle, Version = 6 };
        row.Update(newer, Pricing);

        row.Update(older, Pricing);

        row.State.ShouldBe(SessionState.Working, "posts can be reordered on the way to the UI thread; the version says which snapshot is current");
    }
}
