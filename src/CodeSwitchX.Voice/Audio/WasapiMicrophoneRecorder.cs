namespace CodeSwitchX.Voice.Audio;

/// <summary>
/// Records one microphone at a time. Each recording opens its device in a <see cref="WasapiCaptureSession"/> on the
/// calling thread and closes it with the recording.
/// </summary>
public sealed class WasapiMicrophoneRecorder : IMicrophoneRecorder, IDisposable
{
    /// <summary>The one length limit of a recording: capturing stops by itself here and <see cref="LimitReached"/> says so.</summary>
    private const int MaxSeconds = 120;

    private readonly object _gate = new();
    private Recording? _recording;

    public event EventHandler<CapturedBlock>? BlockCaptured;

    public event EventHandler<MicrophoneException>? Failed;

    public event EventHandler? LimitReached;

    public void Start(string deviceId)
    {
        lock (_gate)
        {
            if (_recording is not null)
            {
                throw new InvalidOperationException("A recording is already running.");
            }

            var recording = new Recording(this);
            recording.Open(deviceId);
            _recording = recording;
        }
    }

    public RecordedClip Stop()
    {
        Recording? recording;
        lock (_gate)
        {
            recording = _recording;
            _recording = null;
        }

        return recording is null ? new RecordedClip([], TimeSpan.Zero) : recording.Finish();
    }

    public void Dispose() => Stop();

    private sealed class Recording(WasapiMicrophoneRecorder owner)
    {
        private readonly List<float> _samples = [];
        private WasapiCaptureSession? _session;
        private int _rate;
        private int _autoStopped;

        // The session is kept in attach, not from Open's return: blocks may arrive before Open returns.
        public void Open(string deviceId) =>
            WasapiCaptureSession.Open(deviceId, session =>
            {
                _session = session;
                _rate = session.SampleRate;
                return OnData;
            }, error => owner.Failed?.Invoke(owner, error));

        public RecordedClip Finish()
        {
            _session!.Finish();
            float[] captured;
            lock (_samples)
            {
                captured = _samples.ToArray();
            }

            var length = TimeSpan.FromSeconds((double)captured.Length / _rate);
            return new RecordedClip(AudioMath.Resample(captured, _rate), length);
        }

        private void OnData(float[] block)
        {
            int count;
            lock (_samples)
            {
                _samples.AddRange(block);
                count = _samples.Count;
            }

            var duration = TimeSpan.FromSeconds((double)block.Length / _rate);
            owner.BlockCaptured?.Invoke(owner, new CapturedBlock(AudioMath.Rms(block), duration));

            if (count >= (long)MaxSeconds * _rate && Interlocked.Exchange(ref _autoStopped, 1) == 0)
            {
                _session!.StopRecording();
                owner.LimitReached?.Invoke(owner, EventArgs.Empty);
            }
        }
    }
}
