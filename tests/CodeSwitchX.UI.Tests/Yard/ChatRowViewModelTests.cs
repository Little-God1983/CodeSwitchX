using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Telemetry;
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
            Model = "claude-sonnet-5", Inferred = true, LastToolName = "Bash", LatestContext = new TokenUsage(100_000, 0, 50_000, 700_000),
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
    public void A_chat_on_the_1M_context_option_measures_its_fill_against_a_million()
    {
        var row = new ChatRowViewModel("s1");
        var pricing = new PricingTable([new PricingRule { Model = "claude-sonnet-4-5", ContextWindow = 200_000 }]);

        row.Update(new SessionSnapshot
        {
            SessionId = "s1", State = SessionState.Working, StartedAt = Now, LastEventAt = Now, StateSince = Now,
            Model = "claude-sonnet-4-5[1m]", LatestContext = new TokenUsage(190_000, 0, 0, 0),
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
            Model = "claude-mythos-6", LatestContext = new TokenUsage(180_000, 0, 0, 0),
        }, PricingTable.Default);

        row.ContextFill.ShouldBe(0.0, "the window of a model without a rule is not known");
        row.Pressure.ShouldBe(ContextPressure.Normal);
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
