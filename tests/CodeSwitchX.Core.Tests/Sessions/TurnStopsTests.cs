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

    private void Turn(string session, SessionState state, SessionState? from = null) => _bus.Publish(new SessionChanged(
        from is { } previous ? Snapshot(session, previous) : null, Snapshot(session, state)));

    private SessionSnapshot Snapshot(string session, SessionState state) => new()
    {
        SessionId = session, State = state, StartedAt = _time.GetUtcNow(), LastEventAt = _time.GetUtcNow(), StateSince = _time.GetUtcNow(),
    };

    [Theory]
    [InlineData("PreToolUse")]
    [InlineData("PostToolUse")]
    public async Task The_chat_s_next_step_takes_the_stop_once(string eventName)
    {
        var stopped = _stops.Request("s1");

        _stops.Take(Step("s1", eventName), true).ShouldBe(TurnStops.Reason);
        _stops.Take(Step("s1", eventName), true).ShouldBeNull("a stop ends one turn");

        (await stopped).ShouldBe(TurnStopOutcome.Stopped);
        _stops.StoppedLately("s1").ShouldBeTrue();
    }

    [Fact]
    public void Another_chat_a_sub_agent_or_an_event_that_is_no_step_leaves_it()
    {
        var stopped = _stops.Request("s1");

        _stops.Take(Step("s2"), true).ShouldBeNull();
        _stops.Take(Step("s1", agent: "agent-7"), true).ShouldBeNull("what a stop does inside a sub-agent was never tried");
        _stops.Take(Step("s1", "UserPromptSubmit", agent: "agent-7"), true).ShouldBeNull();
        _stops.Take(Step("s1", "Notification"), true).ShouldBeNull();

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

        (await stopped).ShouldBe(TurnStopOutcome.TurnEnded);
        _stops.Take(Step("s1"), true).ShouldBeNull("the next turn is not the one that was asked to stop");
        _stops.StoppedLately("s1").ShouldBeFalse();
    }

    [Fact]
    public async Task A_new_prompt_drops_a_stop_its_turn_ended_before_it_was_asked()
    {
        // The Yard still showed the chat working when the stop was asked; its turn had ended, unseen. The user types its
        // next one in the tab: that one is not stopped.
        var stopped = _stops.Request("s1");

        _stops.Take(Step("s1", "UserPromptSubmit"), true).ShouldBeNull();

        (await stopped).ShouldBe(TurnStopOutcome.TurnEnded);
        _stops.Take(Step("s1"), true).ShouldBeNull();
    }

    [Fact]
    public void A_turn_waiting_for_the_user_keeps_the_stop_for_its_next_step()
    {
        var stopped = _stops.Request("s1");

        Turn("s1", SessionState.Waiting, from: SessionState.Working);
        Turn("s1", SessionState.Working, from: SessionState.Waiting);

        stopped.IsCompleted.ShouldBeFalse();
        _stops.Take(Step("s1", "PostToolUse"), true).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_stop_not_taken_for_long_is_dropped()
    {
        var stopped = _stops.Request("s1");

        _time.Advance(TurnStops.Lifetime);

        _stops.Take(Step("s1"), true).ShouldBeNull();
        (await stopped).ShouldBe(TurnStopOutcome.TurnEnded);
    }

    [Fact]
    public async Task An_older_relay_is_never_given_the_stop_which_ends_saying_so()
    {
        // CodeSwitchX started while the chat worked: nothing told it the chat's hooks are an older relay's.
        _stops.CanStop("s1").ShouldBeNull("no step seen yet");
        var stopped = _stops.Request("s1");

        _stops.Take(Step("s1"), relayHandsItOn: false).ShouldBeNull();

        (await stopped).ShouldBe(TurnStopOutcome.OldRelay);
        _stops.CanStop("s1").ShouldBe(false);

        _ = _stops.Request("s1");
        _stops.Take(Step("s1"), relayHandsItOn: true).ShouldNotBeNull("hooks installed again");
        _stops.CanStop("s1").ShouldBe(true);
    }

    [Fact]
    public void Asked_twice_it_is_one_stop()
    {
        _stops.Request("s1").ShouldBeSameAs(_stops.Request("s1"));
    }

    [Fact]
    public void Only_the_stopped_turn_s_end_counts_as_stopped_on_purpose_not_the_next()
    {
        _ = _stops.Request("s1");
        _stops.Take(Step("s1"), true);
        Turn("s1", SessionState.Working, from: SessionState.Working); // a step heard after the stop: still that turn
        Turn("s1", SessionState.Idle, from: SessionState.Working);
        _stops.StoppedLately("s1").ShouldBeTrue("its end is no news");

        Turn("s1", SessionState.Working, from: SessionState.Idle); // told to continue

        _stops.StoppedLately("s1").ShouldBeFalse("the continued turn's end is news again");
    }

    [Fact]
    public void A_stopped_turn_counts_as_stopped_on_purpose_for_a_while_at_most()
    {
        _ = _stops.Request("s1");
        _stops.Take(Step("s1"), true);

        _time.Advance(TurnStops.StoppedFor);

        _stops.StoppedLately("s1").ShouldBeFalse();
    }
}
