namespace CodeSwitchX.UI.Raven;

public enum RavenState
{
    Idle,

    /// <summary>Open mic waits for the user: the microphone is live. The orb shows its light ring.</summary>
    Attending,

    /// <summary>Open mic is paused: the light ring, still and dimmed.</summary>
    AttendingPaused,

    Listening,
    Transcribing,

    /// <summary>Raven's brain is working on an answer.</summary>
    Thinking,

    /// <summary>Raven says its answer out loud; the orb follows what is heard.</summary>
    Speaking,
}
