namespace CodeSwitchX.UI.Raven;

/// <summary>What pressed push to talk: each one is held and let go on its own, and a hold ends when none holds it any more.</summary>
public enum TalkInput
{
    /// <summary>The global push-to-talk chord.</summary>
    Hotkey,

    /// <summary>The panel's mic button: the mouse on it, or Space or Enter while it has the focus.</summary>
    MicButton,
}
