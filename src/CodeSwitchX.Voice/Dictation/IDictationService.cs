namespace CodeSwitchX.Voice.Dictation;

public sealed record DictationResult(string Text, TimeSpan AudioLength);

public interface IDictationService
{
    /// <summary>Transcribes 16 kHz mono float samples. Applies the vocabulary hint and the
    /// corrections. Returns empty text for clips shorter than <see cref="DictationOptions.MinimumClip"/>.
    /// Throws <see cref="DictationModelMissingException"/> when the model file is not on disk.</summary>
    /// <param name="live">This is a preview of speech still in progress, not the clip the user
    /// is waiting on. Live transcriptions arrive every second or so for as long as someone is
    /// talking, so they are logged at Debug: at Information a minute of dictation buries
    /// everything else in the log. The recognition itself is identical.</param>
    Task<DictationResult> TranscribeAsync(ReadOnlyMemory<float> samples,
        DictationVocabulary vocabulary, bool live, CancellationToken ct);

    /// <summary>Loads the model and runs one throwaway clip through it, so that the first real
    /// dictation does not pay for either. Reading a large model off disk and onto the GPU takes
    /// seconds (2.7 s for large-v3-turbo with a warm file cache, more from cold). Runs once per
    /// process: once it has succeeded, a call returns at once. Never runs on the caller's thread.
    ///
    /// <para>Never throws: nothing awaits the result, and a model that will not load is not a
    /// reason to fail. The failure still reaches whoever actually dictates, from
    /// <see cref="TranscribeAsync"/>.</para></summary>
    Task WarmUpAsync(CancellationToken ct);
}

/// <summary>The model has not been downloaded. Its own type so callers can answer "download it first"
/// instead of showing a generic failure.</summary>
public sealed class DictationModelMissingException(string modelPath)
    : Exception($"No Whisper model at {modelPath}. Download it first.")
{
    public string ModelPath { get; } = modelPath;
}

/// <summary>The model file is on disk but Whisper could not load it. A separate type from
/// <see cref="DictationModelMissingException"/> because nothing needs downloading: this model
/// and this machine do not work together, and retrying the same clip cannot help.
///
/// <para>The backend matters enough to be in the message. Whisper.net only walks its runtime
/// order while loading the <em>native library</em>; once, say, the Vulkan one has loaded, the
/// choice is fixed, and a model too large for that device fails here with no CPU fallback left
/// while everything else still reports the GPU as working.</para></summary>
public sealed class DictationModelLoadException(string modelPath, string? runtime, Exception inner)
    : Exception(
        $"Whisper could not load {Path.GetFileName(modelPath)}" +
        (runtime is null ? "" : $" on the {runtime} backend") +
        ". A large model can be more than the graphics card can hold, so try a smaller model. " +
        "A damaged download does the same thing, so deleting the file and " +
        "downloading it again is the other thing worth trying.", inner)
{
    public string ModelPath { get; } = modelPath;

    /// <summary>Backend in use when the load failed, or null if none had been chosen.</summary>
    public string? Runtime { get; } = runtime;
}
