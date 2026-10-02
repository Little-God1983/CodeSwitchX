using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.Core.Tests.Sessions;

public sealed class TurnStopsTests : IDisposable
{
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly FakeTimeProvider _time = new();
    private readonly TurnStops _stops;

    public TurnStopsTests() => _stops = new TurnStops(_bus, _time);

    public void Dispose() => _stops.Dispose();

    private HookEvent Step(string session, string eventName = "PreToolUse", string? agent = null) => new()
    {
        SessionId = session, EventName = eventName, At = _time.GetUtcNow(), ToolName = "Bash", AgentId = agent,
    };

    private void Turn(string session, SessionState state) => _bus.Publish(new SessionChanged(null, new SessionSnapshot
    {
        SessionId = session, State = state, StartedAt = _time.GetUtcNow(), LastEventAt = _time.GetUtcNow(), StateSince = _time.GetUtcNow(),
    }));

    [Theory]
    [InlineData("PreToolUse")]
    [InlineData("PostToolUse")]
    public async Task The_chat_s_next_step_takes_the_stop_once(string eventName)
    {
        var stopped = _stops.Request("s1");

        _stops.Take(Step("s1", eventName), true).ShouldBe(TurnStops.Reason);
        _stops.Take(Step("s1", eventName), true).ShouldBeNull("a stop ends one turn");

        (await stopped).ShouldBeTrue();
        _stops.StoppedLately("s1").ShouldBeTrue();
    }

    [Fact]
    public void Another_chat_a_sub_agent_or_an_event_that_is_no_step_leaves_it()
    {
        var stopped = _stops.Request("s1");

        _stops.Take(Step("s2"), true).ShouldBeNull();
        _stops.Take(Step("s1", agent: "agent-7"), true).ShouldBeNull("what a stop does inside a sub-agent was never tried");
        _stops.Take(Step("s1", "UserPromptSubmit"), true).ShouldBeNull();
        _stops.Take(Step("s1", "Stop"), true).ShouldBeNull();

        stopped.IsCompleted.ShouldBeFalse();
        _stops.Take(Step("s1"), true).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(SessionState.Idle)]
    [InlineData(SessionState.Ended)]
    [InlineData(SessionState.Errored)]
    public async Task A_turn_that_ends_first_drops_the_stop(SessionState end)
    {
        var stopped = _stops.Request("s1");

        Turn("s1", end);

        (await stopped).ShouldBeFalse();
        _stops.Take(Step("s1"), true).ShouldBeNull("the next turn is not the one that was asked to stop");
        _stops.StoppedLately("s1").ShouldBeFalse();
    }

    [Fact]
    public void A_turn_waiting_for_the_user_keeps_the_stop_for_its_next_step()
    {
        var stopped = _stops.Request("s1");

        Turn("s1", SessionState.Waiting);
        Turn("s1", SessionState.Working);

        stopped.IsCompleted.ShouldBeFalse();
        _stops.Take(Step("s1", "PostToolUse"), true).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_stop_not_taken_for_long_is_dropped()
    {
        var stopped = _stops.Request("s1");

        _time.Advance(TurnStops.Lifetime);

        _stops.Take(Step("s1"), true).ShouldBeNull();
        (await stopped).ShouldBeFalse();
    }

    [Fact]
    public void An_older_relay_is_never_given_the_stop_and_its_chat_counts_as_one_that_cannot_be_stopped()
    {
        _stops.CanStop("s1").ShouldBeNull("no step seen yet");
        var stopped = _stops.Request("s1");

        _stops.Take(Step("s1"), relayHandsItOn: false).ShouldBeNull();

        stopped.IsCompleted.ShouldBeFalse("it would never land: not taken, so never said to be");
        _stops.CanStop("s1").ShouldBe(false);
        _stops.Take(Step("s1"), relayHandsItOn: true).ShouldNotBeNull("hooks installed again");
        _stops.CanStop("s1").ShouldBe(true);
    }

    [Fact]
    public void Asked_twice_it_is_one_stop()
    {
        _stops.Request("s1").ShouldBeSameAs(_stops.Request("s1"));
    }

    [Fact]
    public void A_stopped_turn_counts_as_stopped_on_purpose_for_a_while()
    {
        _ = _stops.Request("s1");
        _stops.Take(Step("s1"), true);

        _time.Advance(TurnStops.StoppedFor);

        _stops.StoppedLately("s1").ShouldBeFalse();
    }
}
