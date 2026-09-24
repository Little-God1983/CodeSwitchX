namespace CodeSwitchX.Core.Sessions;

/// <summary>
/// Pure transition table from the design spec. Signals that carry evidence of life
/// (prompt, tool use, notification, stop, session start) are accepted from any state because
/// hooks may be installed while sessions are already running.
/// </summary>
public static class SessionStateMachine
{
    public static bool TryNext(SessionState current, SessionSignal signal, out SessionState next)
    {
        SessionState? candidate = signal switch
        {
            SessionSignal.SessionStart => SessionState.Idle,
            SessionSignal.PromptSubmit => SessionState.Working,
            SessionSignal.ToolUse => SessionState.Working,
            SessionSignal.Notification => SessionState.Waiting,
            SessionSignal.Stop when current != SessionState.Ended => SessionState.Idle,
            SessionSignal.SessionEnd => SessionState.Ended,
            SessionSignal.ProcessGone when current is not (SessionState.Ended or SessionState.Errored) => SessionState.Errored,
            SessionSignal.StaleTimeout when current == SessionState.Idle => SessionState.Stale,
            // No turn runs while claude reports an idle input prompt, so a turn whose Stop never arrived ends here.
            SessionSignal.IdlePrompt when current is SessionState.Working or SessionState.Waiting or SessionState.Starting => SessionState.Idle,
            _ => null,
        };

        if (candidate is null)
        {
            next = current;
            return false;
        }

        next = candidate.Value;
        return true;
    }

    public static SessionState Next(SessionState current, SessionSignal signal)
    {
        TryNext(current, signal, out var next);
        return next;
    }

    public static bool NeedsUser(SessionState state) => state == SessionState.Waiting;

    public static bool IsLive(SessionState state) => state is not (SessionState.Ended or SessionState.Errored);
}
