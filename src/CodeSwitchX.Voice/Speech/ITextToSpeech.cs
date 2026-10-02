namespace CodeSwitchX.Voice.Speech;

/// <summary>
/// Speaks text: the audio comes back in chunks while it is generated, and stops when the call is cancelled. Each engine
/// (Qwen3-TTS, Kokoro) is one (see <see cref="Sidecar.SidecarTextToSpeech"/>), and <see cref="SpeechEngines"/> speaks with
/// the one picked.
/// </summary>
public interface ITextToSpeech
{
    /// <summary>Where the engine stands; <see cref="StatusChanged"/> tells each change.</summary>
    TextToSpeechStatus Status { get; }

    /// <summary>Raised on any thread, never under a lock, for every change of <see cref="Status"/>.</summary>
    event EventHandler<TextToSpeechStatus>? StatusChanged;

    /// <summary>
    /// Gets the engine ready in the background, so the first reply does not wait for it: starts it if it is installed,
    /// and installs it first when <paramref name="install"/> is set. Returns at once and never throws; how it went is in
    /// <see cref="Status"/>. Does nothing while it is getting ready already, is ready, or failed for the same settings.
    /// </summary>
    void Prepare(bool install);

    /// <summary>
    /// Speaks <paramref name="text"/> with the engine, voice and model of <see cref="SpeechSettings"/>; chunks of 16-bit PCM
    /// come as they are generated. Cancelling stops the generation, too.
    /// </summary>
    /// <exception cref="TextToSpeechNotReadyException">The engine is not ready yet (or failed); it is getting ready, if it can.</exception>
    /// <exception cref="TextToSpeechException">The engine failed while speaking.</exception>
    IAsyncEnumerable<SpeechChunk> SpeakAsync(string text, CancellationToken ct);

    /// <summary>
    /// The engine hung (a sentence got no audio in time): it is stopped and started again, so the next sentences do not
    /// wait behind the hung one. Returns at once and never throws.
    /// </summary>
    void Recover();
}

/// <summary>A piece of speech: 16-bit little-endian mono PCM.</summary>
public sealed record SpeechChunk(ReadOnlyMemory<byte> Pcm16, int SampleRate);

public enum TextToSpeechState
{
    /// <summary>Not started, and on disk; it starts when asked to get ready or to speak.</summary>
    Off,

    /// <summary>Not on disk (not installed, or its model not downloaded); it is installed when Raven first speaks, or from the voice setup.</summary>
    NotInstalled,

    /// <summary>No engine is picked: Raven answers in text only, until one is picked in the voice setup or Settings.</summary>
    NoEngine,

    /// <summary>Downloading and installing the engine (a first run).</summary>
    Installing,

    /// <summary>Starting, and loading the model (downloading it on a first run).</summary>
    Loading,

    Ready,

    /// <summary>Installing or loading failed; it is tried again by the next answer once a while has passed, or a new model.</summary>
    Failed,
}

/// <param name="Detail">What it is doing (Installing, Loading) or why it failed (Failed); null otherwise.</param>
/// <param name="Bytes">How far the download of this step of the install is, when it is one of a known size.</param>
public sealed record TextToSpeechStatus(TextToSpeechState State, string? Detail = null, ByteProgress? Bytes = null)
{
    public static readonly TextToSpeechStatus Off = new(TextToSpeechState.Off);
}

public class TextToSpeechException : Exception
{
    public TextToSpeechException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

public sealed class TextToSpeechNotReadyException : TextToSpeechException
{
    public TextToSpeechNotReadyException(TextToSpeechStatus status)
        : base($"The voice is not ready ({status.State}).")
    {
        Status = status;
    }

    public TextToSpeechStatus Status { get; }
}
