namespace CodeSwitchX.Voice.Dictation;

public interface IWhisperModelStore
{
    WhisperModel Model { get; }

    /// <summary>Full path of the model file, whether or not it exists yet.</summary>
    string ModelPath { get; }

    bool IsPresent { get; }

    /// <summary>Size of the installed file, or null when not present.</summary>
    long? SizeBytes { get; }

    /// <summary>Name of the native backend that actually loaded ("Vulkan", "Cuda12", "Cpu"), or
    /// null while no model has been loaded yet, because the choice is made when the first one is.
    /// Lets a host show whether the GPU is in use without guessing.</summary>
    string? LoadedRuntime { get; }

    /// <summary>Downloads the model into place. Progress is 0..1, approximate until the last
    /// byte. A failed or cancelled download leaves no model file behind.</summary>
    Task DownloadAsync(IProgress<double>? progress, CancellationToken ct);
}
