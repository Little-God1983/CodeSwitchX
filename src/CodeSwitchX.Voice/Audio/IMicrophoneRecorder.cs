namespace CodeSwitchX.Voice.Audio;

public sealed record RecordedClip(float[] Samples16k, TimeSpan Length);

/// <summary>One captured block: its RMS and the time it covers. The device decides the block size (10 ms on the RØDE
/// Connect input, measured 2026-09-30), so the duration is carried rather than assumed.</summary>
public readonly record struct CapturedBlock(float Rms, TimeSpan Duration);

public interface IMicrophoneRecorder
{
    /// <exception cref="MicrophoneException">The device could not be opened.</exception>
    void Start(string deviceId);

    /// <summary>Stops recording and returns the clip. Safe when not recording: returns an empty clip.</summary>
    RecordedClip Stop();

    /// <summary>Each captured block; raised on the capture thread. Handlers must not block (never Dispatcher.Invoke, Stop() may be waiting on that thread): marshal asynchronously (Post/BeginInvoke).</summary>
    event EventHandler<CapturedBlock>? BlockCaptured;

    /// <summary>The capture died mid-recording (device unplugged); raised on the capture thread. Same rules as BlockCaptured.</summary>
    event EventHandler<MicrophoneException>? Failed;

    /// <summary>
    /// The recording reached the recorder's length limit and capturing stopped by itself: raised once per recording, on
    /// the capture thread. The clip so far is kept for <see cref="Stop"/>, which the listener still calls. The recorder
    /// alone owns the limit, so nobody else keeps a clock for it. Same rules as BlockCaptured.
    /// </summary>
    event EventHandler? LimitReached;
}
