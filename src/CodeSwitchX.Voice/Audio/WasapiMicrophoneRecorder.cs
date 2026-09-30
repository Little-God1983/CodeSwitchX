using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CodeSwitchX.Voice.Audio;

public sealed class WasapiMicrophoneRecorder : IMicrophoneRecorder, IDisposable
{
    private const int MaxSeconds = 120;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private Session? _session;

    public event EventHandler<float>? BlockCaptured;

    public event EventHandler<MicrophoneException>? Failed;

    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _session is not null;
            }
        }
    }

    public void Start(string deviceId)
    {
        lock (_gate)
        {
            if (_session is not null)
            {
                throw new InvalidOperationException("A recording is already running.");
            }

            MMDevice? device = null;
            WasapiCapture? capture = null;
            try
            {
                device = _enumerator.GetDevice(deviceId);
                capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);
                var session = new Session(this, device, capture);
                capture.DataAvailable += session.OnData;
                capture.RecordingStopped += session.OnStopped;
                capture.StartRecording();
                _session = session;
            }
            catch (Exception e)
            {
                capture?.Dispose();
                device?.Dispose();
                throw new MicrophoneException(MicrophoneFailure.Classify(e), e.Message, e);
            }
        }
    }

    public RecordedClip Stop()
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        return session is null ? new RecordedClip([], TimeSpan.Zero) : session.Finish();
    }

    public void Dispose()
    {
        Stop();
        _enumerator.Dispose();
    }

    private sealed class Session(WasapiMicrophoneRecorder owner, MMDevice device, WasapiCapture capture)
    {
        private readonly List<float> _samples = [];
        private readonly ManualResetEventSlim _stopped = new(false);
        private volatile bool _stoppingOnPurpose;
        private int _autoStopped;

        public void OnData(object? sender, WaveInEventArgs e)
        {
            if (_stoppingOnPurpose)
            {
                return;
            }

            var block = SampleDecoder.ToMonoFloats(e.Buffer, e.BytesRecorded, capture.WaveFormat);
            _samples.AddRange(block);
            owner.BlockCaptured?.Invoke(owner, AudioMath.Rms(block));

            if (_samples.Count >= (long)MaxSeconds * capture.WaveFormat.SampleRate
                && Interlocked.Exchange(ref _autoStopped, 1) == 0)
            {
                capture.StopRecording();
            }
        }

        public void OnStopped(object? sender, StoppedEventArgs e)
        {
            _stopped.Set();
            if (e.Exception is not null && !_stoppingOnPurpose)
            {
                owner.Failed?.Invoke(
                    owner,
                    new MicrophoneException(MicrophoneFailure.Classify(e.Exception), e.Exception.Message, e.Exception));
            }
        }

        public RecordedClip Finish()
        {
            _stoppingOnPurpose = true;
            try
            {
                capture.StopRecording();
            }
            catch (Exception)
            {
                // The device may already be gone; whatever was captured is still returned.
                _stopped.Set();
            }

            _stopped.Wait(StopTimeout);

            var rate = capture.WaveFormat.SampleRate;
            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnStopped;
            capture.Dispose();
            device.Dispose();
            _stopped.Dispose();

            var captured = _samples.ToArray();
            var length = TimeSpan.FromSeconds((double)captured.Length / rate);
            return new RecordedClip(AudioMath.Resample(captured, rate), length);
        }
    }
}
