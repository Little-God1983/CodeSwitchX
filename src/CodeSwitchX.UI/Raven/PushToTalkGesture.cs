namespace CodeSwitchX.UI.Raven;

public enum PushToTalkAction
{
    None,
    Start,
    Stop,
}

/// <summary>Hold to talk, or tap to latch: a press starts, a release after a hold stops, a release after a tap keeps listening until the next press.</summary>
public sealed class PushToTalkGesture(TimeProvider time)
{
    public static readonly TimeSpan HoldThreshold = TimeSpan.FromMilliseconds(350);

    private bool _held;
    private bool _latched;
    private long _pressedAt;

    /// <summary>Start when idle; Stop when latched; None when already held (key autorepeat).</summary>
    public PushToTalkAction Press()
    {
        if (_held)
        {
            return PushToTalkAction.None;
        }

        _held = true;
        if (_latched)
        {
            _latched = false;
            return PushToTalkAction.Stop;
        }

        _pressedAt = time.GetTimestamp();
        return PushToTalkAction.Start;
    }

    /// <summary>Stop after a hold of at least the threshold; None (latched) after a tap; None when not pressed.</summary>
    public PushToTalkAction Release()
    {
        if (!_held)
        {
            return PushToTalkAction.None;
        }

        _held = false;
        if (_pressedAt == 0)
        {
            return PushToTalkAction.None;
        }

        var heldFor = time.GetElapsedTime(_pressedAt);
        _pressedAt = 0;
        if (heldFor >= HoldThreshold)
        {
            return PushToTalkAction.Stop;
        }

        _latched = true;
        return PushToTalkAction.None;
    }

    /// <summary>Forgets any latch or hold, because the recording ended for another reason.</summary>
    public void Reset()
    {
        _held = false;
        _latched = false;
        _pressedAt = 0;
    }
}
