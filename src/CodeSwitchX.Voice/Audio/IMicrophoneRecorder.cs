namespace CodeSwitchX.Voice.Audio;

public sealed record RecordedClip(float[] Samples16k, TimeSpan Length);

/// <summary>One captured block: its RMS and the time it covers. The device decides the block size (10 ms on the RØDE
/// Connect input, measured 2026-09-30), so the duration is carried rather than assumed.</summary>
public readonly record struct CapturedBlock(float Rms, TimeSpan Duration);

public interface IMicrophoneRecorder
{
    bool IsRecording { get; }

    /// <exception cref="MicrophoneException">The device could not be opened.</exception>
    void Start(string deviceId);

    /// <summary>Stops recording and returns the clip. Safe when not recording: returns an empty clip.</summary>
    RecordedClip Stop();

    /// <summary>Each captured block; raised on the capture thread. Handlers must not block (never Dispatcher.Invoke, Stop() may be waiting on that thread): marshal asynchronously (Post/BeginInvoke).</summary>
    event EventHandler<CapturedBlock>? BlockCaptured;

    /// <summary>The capture died mid-recording (device unplugged); raised on the capture thread. Same rules as BlockCaptured.</summary>
    event EventHandler<MicrophoneException>? Failed;
}
