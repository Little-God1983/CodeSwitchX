namespace CodeSwitchX.Voice.Speech.Sidecar;

/// <summary>
/// The Python environment a speech engine's sidecar runs in (Qwen3-TTS, Kokoro); installed on first need, or from the
/// Settings → Voice page.
/// </summary>
public interface ISidecarEnvironment
{
    /// <summary>Installed completely, by this version of the app's recipe. Reads files.</summary>
    bool IsInstalled { get; }

    /// <summary>Whether <paramref name="model"/> is on disk, so that starting it downloads nothing. Reads files.</summary>
    bool HasModel(string model);

    /// <summary>The environment's Python.</summary>
    string Python { get; }

    /// <summary>What the sidecar is told on its command line for <paramref name="model"/> (<c>--model</c>).</summary>
    string ModelArgument(string model);

    /// <summary>Variables the sidecar runs with on top of CodeSwitchX's own (where it downloads models to, say).</summary>
    IReadOnlyDictionary<string, string> Variables { get; }

    /// <summary>Writes the sidecar's scripts into the environment (every start: an update of the app may change them); returns the one to run.</summary>
    string WriteScript();

    /// <summary>Installs it from scratch: what a failed or older install left is removed first.</summary>
    /// <param name="progress">What it is doing, in a few words ("downloading PyTorch"), and how far a download is.</param>
    /// <exception cref="TextToSpeechException">A step failed.</exception>
    Task InstallAsync(IProgress<InstallStep> progress, CancellationToken ct);
}

/// <summary>A step of an install.</summary>
/// <param name="Detail">What it does, in a few words.</param>
/// <param name="Bytes">How far the download of this step is, when it downloads something of a known size.</param>
public sealed record InstallStep(string Detail, ByteProgress? Bytes = null);
