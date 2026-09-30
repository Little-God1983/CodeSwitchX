using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CodeSwitchX.Voice.Audio;

/// <summary>
/// Records one microphone at a time. Every recording gets its own device enumerator, made in <see cref="Start"/> on the
/// calling thread (the panel starts from the thread pool) and released with the recording: one made with the recorder
/// would live on the UI thread that DI builds it on, and calls on it from the pool could be marshalled back to that
/// thread, or fail to marshal at all.
/// </summary>
public sealed class WasapiMicrophoneRecorder : IMicrophoneRecorder, IDisposable
{
    /// <summary>The one length limit of a recording: capturing stops by itself here and <see cref="LimitReached"/> says so.</summary>
    private const int MaxSeconds = 120;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private Session? _session;

    public event EventHandler<CapturedBlock>? BlockCaptured;

    public event EventHandler<MicrophoneException>? Failed;

    public event EventHandler? LimitReached;

    public void Start(string deviceId)
    {
        lock (_gate)
        {
            if (_session is not null)
            {
                throw new InvalidOperationException("A recording is already running.");
            }

            MMDeviceEnumerator? enumerator = null;
            MMDevice? device = null;
            WasapiCapture? capture = null;
            try
            {
                enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDevice(deviceId);
                capture = CreateCapture(device);
                var session = new Session(this, enumerator, device, capture);
                capture.DataAvailable += session.OnData;
                capture.RecordingStopped += session.OnStopped;
                capture.StartRecording();
                _session = session;
            }
            catch (Exception e)
            {
                capture?.Dispose();
                device?.Dispose();
                enumerator?.Dispose();
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

    public void Dispose() => Stop();

    // WasapiCapture remembers SynchronizationContext.Current and posts RecordingStopped through it. Built on the UI thread
    // that would queue the event behind a Stop() that is waiting for it, so build it with no context: every event then
    // stays on the capture thread.
    private static WasapiCapture CreateCapture(MMDevice device)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class Session(WasapiMicrophoneRecorder owner, MMDeviceEnumerator enumerator, MMDevice device, WasapiCapture capture)
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
            int count;
            lock (_samples)
            {
                _samples.AddRange(block);
                count = _samples.Count;
            }

            var duration = TimeSpan.FromSeconds((double)block.Length / capture.WaveFormat.SampleRate);
            owner.BlockCaptured?.Invoke(owner, new CapturedBlock(AudioMath.Rms(block), duration));

            if (count >= (long)MaxSeconds * capture.WaveFormat.SampleRate
                && Interlocked.Exchange(ref _autoStopped, 1) == 0)
            {
                capture.StopRecording();
                owner.LimitReached?.Invoke(owner, EventArgs.Empty);
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

            var stopped = _stopped.Wait(StopTimeout);
            var rate = capture.WaveFormat.SampleRate;
            float[] captured;
            lock (_samples)
            {
                captured = _samples.ToArray();
            }

            if (stopped)
            {
                Release();
            }
            else
            {
                // The capture thread did not finish in time. Disposing joins that thread, so never do that on the caller.
                _ = Task.Run(Release);
            }

            var length = TimeSpan.FromSeconds((double)captured.Length / rate);
            return new RecordedClip(AudioMath.Resample(captured, rate), length);
        }

        private void Release()
        {
            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnStopped;
            capture.Dispose();
            device.Dispose();
            enumerator.Dispose();
            _stopped.Dispose();
        }
    }
}
