namespace CodeSwitchX.Voice.Dictation;

public interface IWhisperModelStore
{
    /// <summary>The model dictation uses; Settings changes it while the app runs.</summary>
    WhisperModel Model { get; set; }

    /// <summary>Raised when <see cref="Model"/> changes, on the thread that changed it.</summary>
    event EventHandler? ModelChanged;

    /// <summary>Full path of the model file, whether or not it exists yet.</summary>
    string ModelPath { get; }

    bool IsPresent { get; }

    /// <summary>Name of the native backend that actually loaded ("Vulkan", "Cuda12", "Cpu"), or
    /// null while no model has been loaded yet, because the choice is made when the first one is.
    /// Lets a host show whether the GPU is in use without guessing.</summary>
    string? LoadedRuntime { get; }

    /// <summary>Downloads the model in use into place. Progress is 0..1, approximate until the last
    /// byte. A failed download leaves no model file behind. A caller asking while it downloads
    /// waits for that same download; <paramref name="ct"/> stops the wait, not the download.</summary>
    Task DownloadAsync(IProgress<double>? progress, CancellationToken ct);

    /// <summary>The download going on, if any.</summary>
    ModelDownload? Download { get; }

    /// <summary>Raised as a download starts, about once a megabyte while it runs, and as it ends; on any thread.</summary>
    event EventHandler? DownloadChanged;
}

/// <summary>A model downloading, and how far it is (approximate until the last byte).</summary>
public sealed record ModelDownload(WhisperModel Model, ByteProgress Bytes);
