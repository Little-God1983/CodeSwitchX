using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.Settings;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Telemetry;

public class PerformanceBarViewModelTests
{
    private readonly ShellTestHarness _h = new();

    [Fact]
    public async Task Snapshot_is_formatted_for_the_bar()
    {
        _h.Settings.GetAsync<long?>(SettingKeys.FiveHourBudgetTokens, Arg.Any<CancellationToken>()).Returns(Task.FromResult<long?>(2_000_000));
        var bar = _h.Shell.PerformanceBar;
        await bar.InitializeAsync(CancellationToken.None);
        var rate = new long[60];
        rate[^1] = 500;
        rate[^2] = 1000;

        bar.Apply(new TelemetrySnapshot(
            new UsageTotals(new TokenUsage(1_200_000, 34_000, 0, 0, 0), 3.456m),
            new UsageTotals(new TokenUsage(1_000_000, 0, 0, 0, 0), 2m),
            rate,
            _h.Time.GetUtcNow()));

        bar.TokensTodayText.ShouldBe("1.2M");
        bar.CostTodayText.ShouldBe("$3.46 est.");
        bar.HasBudget.ShouldBeTrue();
        bar.FiveHourText.ShouldBe("1M / 2M");
        bar.FiveHourPercent.ShouldBe(50);
        bar.RateNormalized.Length.ShouldBe(60);
        bar.RateNormalized[^2].ShouldBe(1.0);
        bar.RateNormalized[^1].ShouldBe(0.5);
    }

    [Fact]
    public async Task Without_a_budget_the_five_hour_figure_stands_alone()
    {
        var bar = _h.Shell.PerformanceBar;
        await bar.InitializeAsync(CancellationToken.None);

        bar.Apply(new TelemetrySnapshot(UsageTotals.Zero, new UsageTotals(new TokenUsage(750_000, 0, 0, 0, 0), 0m), new long[60], _h.Time.GetUtcNow()));

        bar.HasBudget.ShouldBeFalse();
        bar.FiveHourText.ShouldBe("750K");
        bar.FiveHourPercent.ShouldBe(0);
    }

    [Fact]
    public async Task Session_counts_follow_the_engine()
    {
        var bar = _h.Shell.PerformanceBar;
        await bar.InitializeAsync(CancellationToken.None);
        var now = _h.Time.GetUtcNow();
        _h.Resolver.SetRoots(WorkspaceResolver.RootsOf([_h.App]));

        _h.Engine.Apply(new HookEvent { SessionId = "a", EventName = "UserPromptSubmit", Signal = SessionSignal.PromptSubmit, At = now, Cwd = _h.App.RootPath });
        _h.Engine.Apply(new HookEvent { SessionId = "b", EventName = "Notification", Signal = SessionSignal.Notification, At = now, Cwd = _h.App.RootPath });
        _h.Engine.Apply(new HookEvent { SessionId = "c", EventName = "SessionEnd", Signal = SessionSignal.SessionEnd, At = now, Cwd = _h.App.RootPath });

        bar.ActiveSessions.ShouldBe(2);
        bar.WaitingSessions.ShouldBe(1);
    }

    [Fact]
    public async Task Active_chats_are_the_live_rows_of_the_yard_not_stale_ones_or_chats_on_no_tile()
    {
        var bar = _h.Shell.PerformanceBar;
        await bar.InitializeAsync(CancellationToken.None);
        var now = _h.Time.GetUtcNow();
        _h.Resolver.SetRoots(WorkspaceResolver.RootsOf([_h.App]));
        _h.Engine.Apply(new HookEvent { SessionId = "on-tile", EventName = "UserPromptSubmit", Signal = SessionSignal.PromptSubmit, At = now, Cwd = _h.App.RootPath });
        _h.Engine.Apply(new HookEvent { SessionId = "elsewhere", EventName = "UserPromptSubmit", Signal = SessionSignal.PromptSubmit, At = now, Cwd = @"c:\notes" });
        _h.Engine.Restore([new SessionSnapshot { SessionId = "old", WorkspaceId = _h.App.Id, State = SessionState.Stale, StartedAt = now.AddDays(-1), LastEventAt = now.AddDays(-1), StateSince = now.AddHours(-23) }]);

        bar.RecountSessions();

        bar.ActiveSessions.ShouldBe(1, "a Stale chat and a chat on no tile are not rows on the Yard, and the count grew over the 24 h restore window");
    }

    [Fact]
    public async Task Stale_rows_still_on_a_tile_count_and_a_waiting_chat_on_no_tile_counts_nowhere()
    {
        // A tile keeps a Stale row for 30 minutes; "active" must match those rows, and "waiting" must not name a chat no tile shows.
        var bar = _h.Shell.PerformanceBar;
        await bar.InitializeAsync(CancellationToken.None);
        var now = _h.Time.GetUtcNow();
        _h.Resolver.SetRoots(WorkspaceResolver.RootsOf([_h.App]));
        _h.Engine.Restore([
            new SessionSnapshot { SessionId = "shown", WorkspaceId = _h.App.Id, State = SessionState.Stale, Title = "fix the build", StartedAt = now.AddHours(-2), LastEventAt = now.AddHours(-1), StateSince = now.AddMinutes(-10) },
            new SessionSnapshot { SessionId = "gone", WorkspaceId = _h.App.Id, State = SessionState.Stale, StartedAt = now.AddHours(-3), LastEventAt = now.AddHours(-2), StateSince = now.AddMinutes(-40) },
        ]);
        _h.Engine.Apply(new HookEvent { SessionId = "elsewhere", EventName = "Notification", Signal = SessionSignal.Notification, At = now, Cwd = @"c:\notes" });

        bar.RecountSessions();

        bar.ActiveSessions.ShouldBe(1, "the Stale row of ten minutes is still on the tile, the one of forty is not");
        bar.WaitingSessions.ShouldBe(0, "a waiting chat on no tile is shown nowhere");
    }

    [Fact]
    public async Task A_session_that_is_no_chat_yet_counts_nowhere()
    {
        var bar = _h.Shell.PerformanceBar;
        var yard = _h.Shell.Yard;
        _h.Resolver.SetRoots(WorkspaceResolver.RootsOf([_h.App]));
        await yard.InitializeAsync(CancellationToken.None);
        await bar.InitializeAsync(CancellationToken.None);
        yard.NeedsMeFirst = true;

        _h.Engine.Apply(new HookEvent { SessionId = "ghost", EventName = "SessionStart", Signal = SessionSignal.SessionStart, At = _h.Time.GetUtcNow(), Cwd = _h.App.RootPath });

        var tile = yard.FindTile(_h.App.Id)!;
        tile.Chats.ShouldBeEmpty();
        tile.NeedsAttention.ShouldBeFalse();
        tile.AttentionRank.ShouldBe(2);
        bar.ActiveSessions.ShouldBe(0, "an idle session that was never prompted is not a chat yet");
        bar.WaitingSessions.ShouldBe(0);

        _h.Engine.Apply(new HookEvent { SessionId = "ghost", EventName = "UserPromptSubmit", Signal = SessionSignal.PromptSubmit, At = _h.Time.GetUtcNow(), Cwd = _h.App.RootPath });

        tile.Chats.ShouldHaveSingleItem();
        tile.AttentionRank.ShouldBe(1);
        bar.ActiveSessions.ShouldBe(1, "the prompt makes it a chat");
    }

    [Fact]
    public async Task The_cost_says_when_it_leaves_out_the_usage_of_a_model_without_a_price()
    {
        var bar = _h.Shell.PerformanceBar;
        await bar.InitializeAsync(CancellationToken.None);

        bar.Apply(new TelemetrySnapshot(
            new UsageTotals(new TokenUsage(1_200_000, 0, 0, 0, 0), 3.456m) { Unpriced = true }, UsageTotals.Zero, new long[60], _h.Time.GetUtcNow()));

        bar.CostTodayText.ShouldBe("$3.46 est. incl. unpriced");
    }
}
