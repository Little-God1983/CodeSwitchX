using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Core.Tests.Sessions;

public class SessionStateMachineTests
{
    [Theory]
    // spec diagram
    [InlineData(SessionState.Starting, SessionSignal.SessionStart, SessionState.Idle)]
    [InlineData(SessionState.Idle, SessionSignal.PromptSubmit, SessionState.Working)]
    [InlineData(SessionState.Working, SessionSignal.ToolUse, SessionState.Working)]
    [InlineData(SessionState.Working, SessionSignal.Notification, SessionState.Waiting)]
    [InlineData(SessionState.Waiting, SessionSignal.ToolUse, SessionState.Working)]
    [InlineData(SessionState.Waiting, SessionSignal.PromptSubmit, SessionState.Working)]
    [InlineData(SessionState.Working, SessionSignal.Stop, SessionState.Idle)]
    [InlineData(SessionState.Idle, SessionSignal.StaleTimeout, SessionState.Stale)]
    [InlineData(SessionState.Working, SessionSignal.ProcessGone, SessionState.Errored)]
    [InlineData(SessionState.Idle, SessionSignal.SessionEnd, SessionState.Ended)]
    // hooks installed mid-session: evidence of life revives any state
    [InlineData(SessionState.Starting, SessionSignal.PromptSubmit, SessionState.Working)]
    [InlineData(SessionState.Stale, SessionSignal.PromptSubmit, SessionState.Working)]
    [InlineData(SessionState.Stale, SessionSignal.ToolUse, SessionState.Working)]
    [InlineData(SessionState.Errored, SessionSignal.ToolUse, SessionState.Working)]
    [InlineData(SessionState.Idle, SessionSignal.ToolUse, SessionState.Working)]
    [InlineData(SessionState.Idle, SessionSignal.Notification, SessionState.Waiting)]
    [InlineData(SessionState.Waiting, SessionSignal.Stop, SessionState.Idle)]
    [InlineData(SessionState.Working, SessionSignal.SessionEnd, SessionState.Ended)]
    [InlineData(SessionState.Waiting, SessionSignal.SessionEnd, SessionState.Ended)]
    [InlineData(SessionState.Idle, SessionSignal.ProcessGone, SessionState.Errored)]
    [InlineData(SessionState.Waiting, SessionSignal.ProcessGone, SessionState.Errored)]
    [InlineData(SessionState.Working, SessionSignal.SessionStart, SessionState.Idle)]
    // idle_prompt fires only after a turn has finished, so it ends a turn whose Stop never arrived
    [InlineData(SessionState.Working, SessionSignal.IdlePrompt, SessionState.Idle)]
    [InlineData(SessionState.Waiting, SessionSignal.IdlePrompt, SessionState.Idle)]
    [InlineData(SessionState.Starting, SessionSignal.IdlePrompt, SessionState.Idle)]
    public void Defined_transitions(SessionState from, SessionSignal signal, SessionState expected)
    {
        SessionStateMachine.TryNext(from, signal, out var next).ShouldBeTrue();
        next.ShouldBe(expected);
        SessionStateMachine.Next(from, signal).ShouldBe(expected);
    }

    [Theory]
    [InlineData(SessionState.Working, SessionSignal.StaleTimeout)]
    [InlineData(SessionState.Waiting, SessionSignal.StaleTimeout)]
    [InlineData(SessionState.Ended, SessionSignal.StaleTimeout)]
    [InlineData(SessionState.Ended, SessionSignal.ProcessGone)]
    [InlineData(SessionState.Ended, SessionSignal.Stop)]
    [InlineData(SessionState.Errored, SessionSignal.ProcessGone)]
    [InlineData(SessionState.Stale, SessionSignal.StaleTimeout)]
    [InlineData(SessionState.Idle, SessionSignal.IdlePrompt)]
    [InlineData(SessionState.Stale, SessionSignal.IdlePrompt)]
    [InlineData(SessionState.Ended, SessionSignal.IdlePrompt)]
    [InlineData(SessionState.Errored, SessionSignal.IdlePrompt)]
    public void Undefined_transitions_keep_the_state(SessionState from, SessionSignal signal)
    {
        SessionStateMachine.TryNext(from, signal, out var next).ShouldBeFalse();
        next.ShouldBe(from);
        SessionStateMachine.Next(from, signal).ShouldBe(from);
    }

    [Fact]
    public void Every_state_and_signal_pair_is_total()
    {
        foreach (var state in Enum.GetValues<SessionState>())
        {
            foreach (var signal in Enum.GetValues<SessionSignal>())
            {
                Should.NotThrow(() => SessionStateMachine.Next(state, signal));
            }
        }
    }

    [Fact]
    public void Waiting_is_the_only_state_that_needs_the_user()
    {
        Enum.GetValues<SessionState>().Where(SessionStateMachine.NeedsUser)
            .ShouldBe(new[] { SessionState.Waiting });
    }

    [Fact]
    public void Live_states_exclude_ended_and_errored()
    {
        Enum.GetValues<SessionState>().Where(SessionStateMachine.IsLive).ShouldBe(
            new[] { SessionState.Starting, SessionState.Idle, SessionState.Working, SessionState.Waiting, SessionState.Stale });
    }
}
