namespace CodeSwitchX.Voice.Audio;

public interface IMicrophoneCatalog
{
    /// <summary>The active capture endpoints.</summary>
    IReadOnlyList<MicrophoneDevice> List();

    /// <summary>The Windows default capture device (Communications role, then Console), or null when there is none.</summary>
    MicrophoneDevice? Default();

    /// <summary>Raised on any thread when a capture device appears, disappears or becomes the default.</summary>
    event EventHandler? DevicesChanged;
}
