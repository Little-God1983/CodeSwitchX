namespace CodeSwitchX.UI.Raven;

public enum RavenState
{
    Idle,
    Listening,
    Transcribing,

    /// <summary>Raven's brain is working on an answer.</summary>
    Thinking,
}
