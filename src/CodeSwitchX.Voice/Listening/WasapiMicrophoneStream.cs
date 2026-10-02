using CodeSwitchX.Voice.Audio;

namespace CodeSwitchX.Voice.Listening;

/// <summary>
/// Open mic's capture: the device opens as <see cref="WasapiMicrophoneRecorder"/>'s does (a <see cref="WasapiCaptureSession"/>),
/// but every block goes out at 16 kHz as it comes and nothing is kept, so it can run for hours.
/// </summary>
public sealed class WasapiMicrophoneStream : IMicrophoneStream, IDisposable
{
    private readonly object _gate = new();
    private WasapiCaptureSession? _session;

    public event EventHandler<CapturedFrames>? FramesCaptured;

    public event EventHandler<MicrophoneException>? Failed;

    public void Start(string deviceId)
    {
        lock (_gate)
        {
            if (_session is not null)
            {
                throw new InvalidOperationException("The stream is already running.");
            }

            _session = WasapiCaptureSession.Open(deviceId, session =>
            {
                var resampler = new StreamResampler(session.SampleRate);
                return block => FramesCaptured?.Invoke(this, new CapturedFrames(resampler.Push(block), AudioMath.Rms(block)));
            }, error => Failed?.Invoke(this, error));
        }
    }

    public void Stop()
    {
        WasapiCaptureSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        session?.Finish();
    }

    public void Dispose() => Stop();
}
