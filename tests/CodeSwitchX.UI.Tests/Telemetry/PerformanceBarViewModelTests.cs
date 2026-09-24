using CodeSwitchX.Core.Sessions;
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
            new UsageTotals(new TokenUsage(1_200_000, 34_000, 0, 0), 3.456m),
            new UsageTotals(new TokenUsage(1_000_000, 0, 0, 0), 2m),
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

        bar.Apply(new TelemetrySnapshot(UsageTotals.Zero, new UsageTotals(new TokenUsage(750_000, 0, 0, 0), 0m), new long[60], _h.Time.GetUtcNow()));

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

        _h.Engine.Apply(new HookEvent { SessionId = "a", EventName = "UserPromptSubmit", Signal = SessionSignal.PromptSubmit, At = now });
        _h.Engine.Apply(new HookEvent { SessionId = "b", EventName = "Notification", Signal = SessionSignal.Notification, At = now });
        _h.Engine.Apply(new HookEvent { SessionId = "c", EventName = "SessionEnd", Signal = SessionSignal.SessionEnd, At = now });

        bar.ActiveSessions.ShouldBe(2);
        bar.WaitingSessions.ShouldBe(1);
    }
}
