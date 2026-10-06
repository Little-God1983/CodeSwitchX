namespace CodeSwitchX.Voice.Audio;

public interface ISpeakerCatalog
{
    /// <summary>The active output endpoints.</summary>
    IReadOnlyList<SpeakerDevice> List();

    /// <summary>The Windows default output device (the default playback device, then the default communications device), or null when there is none.</summary>
    SpeakerDevice? Default();

    /// <summary>Raised on any thread when an output device appears, disappears or becomes the default. Handlers must not block: marshal asynchronously (Post/BeginInvoke).</summary>
    event EventHandler? DevicesChanged;
}
