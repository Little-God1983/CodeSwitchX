using CodeSwitchX.Core.Messaging;

namespace CodeSwitchX.Core.Sessions;

/// <summary>What came of a stop asked for a chat's turn.</summary>
public enum TurnStopOutcome
{
    /// <summary>The chat's relay took it: the turn ended at that step.</summary>
    Stopped,

    /// <summary>The turn ended first (the chat idled, ended, failed, or started a new turn), or the stop expired.</summary>
    TurnEnded,

    /// <summary>The chat's hooks are an older CodeSwitchX's relay, which drops a stop: it would never land.</summary>
    OldRelay,
}

/// <summary>
/// Stops asked for a chat's running turn, until its hook relay takes one. Nothing outside a VS Code chat's tab can
/// interrupt it, but a hook can end its turn: the chat's next <c>PreToolUse</c> or <c>PostToolUse</c> takes the stop
/// (<see cref="Take"/>) and answers Claude Code <c>continue: false</c>, and on <c>PreToolUse</c> denies the step it was
/// about to take. So a stop lands at the chat's next tool step: a turn that only writes, or one in a long step, runs on
/// until then. Only the main agent's events take it: what a stop does inside a sub-agent was never tried. A stop whose
/// turn ends first (the chat idles, ends, fails, or starts a new turn) is dropped, and so is one older than
/// <see cref="Lifetime"/>. Thread-safe: hooks and the bus come on any thread.
/// </summary>
public sealed class TurnStops : IDisposable
{
    /// <summary>A stop not taken within this is dropped: the turn it was for is long over.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>The longest a turn stopped here counts as stopped on purpose (<see cref="StoppedLately"/>); the chat's next turn ends it sooner.</summary>
    public static readonly TimeSpan StoppedFor = TimeSpan.FromMinutes(2);

    /// <summary>What the chat is told, and shows on the step it did not take.</summary>
    public const string Reason = "Stopped by the user through Raven (CodeSwitchX).";

    private readonly TimeProvider _time;
    private readonly IDisposable _subscription;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _stopped = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _relayHandsItOn = new(StringComparer.Ordinal);

    public TurnStops(IEventBus bus, TimeProvider time)
    {
        _time = time;
        _subscription = bus.Subscribe<SessionChanged>(Changed);
    }

    /// <summary>
    /// Asks the chat's running turn to stop. The task completes with what came of it; asked again before then, it is the
    /// same stop.
    /// </summary>
    public Task<TurnStopOutcome> Request(string sessionId)
    {
        lock (_lock)
        {
            if (!_pending.TryGetValue(sessionId, out var pending) || Expired(pending))
            {
                pending?.Done.TrySetResult(TurnStopOutcome.TurnEnded);
                pending = new Pending(_time.GetUtcNow(), new TaskCompletionSource<TurnStopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously));
                _pending[sessionId] = pending;
            }

            return pending.Done.Task;
        }
    }

    /// <summary>
    /// The stop for this hook event, taken; null when none is asked for it, or the event cannot carry one. The event is
    /// published before this is asked, so a new turn it starts has already dropped a stop asked for the turn before. A
    /// relay that does not hand a stop on (an older
    /// CodeSwitchX's, still in Claude Code's settings) is never given it: the stop ends as <see cref="TurnStopOutcome.OldRelay"/>,
    /// and the chat is noted as one that cannot be stopped (<see cref="CanStop"/>).
    /// </summary>
    /// <param name="relayHandsItOn">Whether the relay that sent the event tells Claude Code a stop.</param>
    public string? Take(HookEvent hookEvent, bool relayHandsItOn)
    {
        if (hookEvent.EventName is not ("PreToolUse" or "PostToolUse") || hookEvent.AgentId is not null)
        {
            return null;
        }

        lock (_lock)
        {
            _relayHandsItOn[hookEvent.SessionId] = relayHandsItOn;
            if (!relayHandsItOn)
            {
                End(hookEvent.SessionId, TurnStopOutcome.OldRelay);
                return null;
            }

            if (!_pending.Remove(hookEvent.SessionId, out var pending))
            {
                return null;
            }

            if (Expired(pending))
            {
                pending.Done.TrySetResult(TurnStopOutcome.TurnEnded);
                return null;
            }

            _stopped[hookEvent.SessionId] = _time.GetUtcNow();
            pending.Done.TrySetResult(TurnStopOutcome.Stopped);
            return Reason;
        }
    }

    /// <summary>
    /// Whether the chat's hooks hand a stop on, as its last tool step showed; null before it took one while the app ran.
    /// False means Claude Code runs an older CodeSwitchX's relay: a stop would never land.
    /// </summary>
    public bool? CanStop(string sessionId)
    {
        lock (_lock)
        {
            return _relayHandsItOn.TryGetValue(sessionId, out var can) ? can : null;
        }
    }

    /// <summary>Whether the chat's last turn ended because it was stopped here, and no new turn began since: its end is no news.</summary>
    public bool StoppedLately(string sessionId)
    {
        lock (_lock)
        {
            return _stopped.TryGetValue(sessionId, out var at) && _time.GetUtcNow() - at < StoppedFor;
        }
    }

    private void Changed(SessionChanged change)
    {
        var id = change.Current.SessionId;
        var state = change.Current.State;
        lock (_lock)
        {
            if (state == SessionState.Working)
            {
                // A new turn (told to continue, say, or typed in the tab): the stopped one is over, and this one's end is news
                // again. A stop still asked was for a turn that ended unseen (the Yard lags the bus): it must not cut this one off.
                if (change.Previous?.State is not (SessionState.Working or SessionState.Waiting))
                {
                    _stopped.Remove(id);
                    End(id, TurnStopOutcome.TurnEnded);
                }

                return;
            }

            // Waiting is still the turn (a question, a permission): the step after it takes the stop.
            if (state == SessionState.Waiting)
            {
                return;
            }

            End(id, TurnStopOutcome.TurnEnded);
            if (!SessionStateMachine.IsLive(state))
            {
                _relayHandsItOn.Remove(id);
            }

            foreach (var old in _stopped.Where(s => _time.GetUtcNow() - s.Value >= StoppedFor).Select(s => s.Key).ToList())
            {
                _stopped.Remove(old);
            }
        }
    }

    /// <summary>Ends the chat's pending stop, if any, with what came of it. Under the lock.</summary>
    private void End(string sessionId, TurnStopOutcome outcome)
    {
        if (_pending.Remove(sessionId, out var pending))
        {
            pending.Done.TrySetResult(outcome);
        }
    }

    private bool Expired(Pending pending) => _time.GetUtcNow() - pending.Since >= Lifetime;

    public void Dispose() => _subscription.Dispose();

    private sealed record Pending(DateTimeOffset Since, TaskCompletionSource<TurnStopOutcome> Done);
}
