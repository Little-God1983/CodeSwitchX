using CodeSwitchX.Voice.Audio;

namespace CodeSwitchX.Voice.Listening;

/// <summary>A captured block, resampled to 16 kHz mono, with its level.</summary>
public sealed record CapturedFrames(float[] Samples16k, float Rms);

/// <summary>
/// A microphone that captures until stopped and keeps nothing: Open mic runs for hours. Its events are raised on the
/// capture thread and must not block it (queue, or post asynchronously).
/// </summary>
public interface IMicrophoneStream
{
    /// <exception cref="MicrophoneException">The device could not be opened.</exception>
    void Start(string deviceId);

    /// <summary>Stops capturing; safe when not started. Waits up to 2 s for the capture thread.</summary>
    void Stop();

    event EventHandler<CapturedFrames>? FramesCaptured;

    /// <summary>The capture died (the device was unplugged); it has stopped.</summary>
    event EventHandler<MicrophoneException>? Failed;
}
