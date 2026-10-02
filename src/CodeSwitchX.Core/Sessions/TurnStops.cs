using CodeSwitchX.Core.Messaging;

namespace CodeSwitchX.Core.Sessions;

/// <summary>
/// Stops asked for a chat's running turn, until its hook relay takes one. Nothing outside a VS Code chat's tab can
/// interrupt it, but a hook can end its turn: the chat's next <c>PreToolUse</c> or <c>PostToolUse</c> takes the stop
/// (<see cref="Take"/>) and answers Claude Code <c>continue: false</c>, and on <c>PreToolUse</c> denies the step it was
/// about to take. So a stop lands at the chat's next tool step: a turn that only writes, or one in a long step, runs on
/// until then. Only the main agent's events take it: what a stop does inside a sub-agent was never tried. A stop whose
/// turn ends first (the chat idles, ends, or fails) is dropped, and so is one older than <see cref="Lifetime"/>.
/// Thread-safe: hooks and the bus come on any thread.
/// </summary>
public sealed class TurnStops : IDisposable
{
    /// <summary>A stop not taken within this is dropped: the turn it was for is long over.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>How long a turn stopped here counts as stopped on purpose (<see cref="StoppedLately"/>).</summary>
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
        _subscription = bus.Subscribe<SessionChanged>(TurnMayHaveEnded);
    }

    /// <summary>
    /// Asks the chat's running turn to stop. The task is true once the chat's relay took the stop, false when its turn
    /// ended first or the stop expired; asked again before then, it is the same stop.
    /// </summary>
    public Task<bool> Request(string sessionId)
    {
        lock (_lock)
        {
            if (!_pending.TryGetValue(sessionId, out var pending) || Expired(pending))
            {
                pending?.Done.TrySetResult(false);
                pending = new Pending(_time.GetUtcNow(), new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
                _pending[sessionId] = pending;
            }

            return pending.Done.Task;
        }
    }

    /// <summary>
    /// The stop for this hook event, taken; null when none is asked for it, or the event cannot carry one. A relay that
    /// does not hand a stop on (an older CodeSwitchX's, still in Claude Code's settings) is never given it, and the chat is
    /// noted as one that cannot be stopped (<see cref="CanStop"/>).
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
            if (!relayHandsItOn || !_pending.Remove(hookEvent.SessionId, out var pending))
            {
                return null;
            }

            if (Expired(pending))
            {
                pending.Done.TrySetResult(false);
                return null;
            }

            _stopped[hookEvent.SessionId] = _time.GetUtcNow();
            pending.Done.TrySetResult(true);
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

    /// <summary>Whether the chat's turn ended lately because it was stopped here: its end is no news.</summary>
    public bool StoppedLately(string sessionId)
    {
        lock (_lock)
        {
            return _stopped.TryGetValue(sessionId, out var at) && _time.GetUtcNow() - at < StoppedFor;
        }
    }

    private void TurnMayHaveEnded(SessionChanged change)
    {
        // Waiting is still the turn (a question, a permission): the step after it takes the stop.
        if (change.Current.State is SessionState.Working or SessionState.Waiting)
        {
            return;
        }

        lock (_lock)
        {
            if (_pending.Remove(change.Current.SessionId, out var pending))
            {
                pending.Done.TrySetResult(false);
            }

            if (!SessionStateMachine.IsLive(change.Current.State))
            {
                _relayHandsItOn.Remove(change.Current.SessionId);
            }

            foreach (var old in _stopped.Where(s => _time.GetUtcNow() - s.Value >= StoppedFor).Select(s => s.Key).ToList())
            {
                _stopped.Remove(old);
            }
        }
    }

    private bool Expired(Pending pending) => _time.GetUtcNow() - pending.Since >= Lifetime;

    public void Dispose() => _subscription.Dispose();

    private sealed record Pending(DateTimeOffset Since, TaskCompletionSource<bool> Done);
}
