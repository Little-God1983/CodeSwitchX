namespace CodeSwitchX.Voice.Audio;

public sealed record RecordedClip(float[] Samples16k, TimeSpan Length);

public interface IMicrophoneRecorder
{
    bool IsRecording { get; }

    /// <exception cref="MicrophoneException">The device could not be opened.</exception>
    void Start(string deviceId);

    /// <summary>Stops recording and returns the clip. Safe when not recording: returns an empty clip.</summary>
    RecordedClip Stop();

    /// <summary>RMS of each captured block; raised on the capture thread. Handlers must not block (never Dispatcher.Invoke, Stop() may be waiting on that thread): marshal asynchronously (Post/BeginInvoke).</summary>
    event EventHandler<float>? BlockCaptured;

    /// <summary>The capture died mid-recording (device unplugged); raised on the capture thread. Same rules as BlockCaptured.</summary>
    event EventHandler<MicrophoneException>? Failed;
}
