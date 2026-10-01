using CodeSwitchX.Voice.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CodeSwitchX.Voice.Listening;

/// <summary>
/// Open mic's capture: as <see cref="WasapiMicrophoneRecorder"/> opens a device (an enumerator per stream made on the
/// calling thread, the capture built with no synchronisation context), but every block goes out at 16 kHz as it comes
/// and nothing is kept, so it can run for hours.
/// </summary>
public sealed class WasapiMicrophoneStream : IMicrophoneStream, IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private Session? _session;

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

            MMDeviceEnumerator? enumerator = null;
            MMDevice? device = null;
            WasapiCapture? capture = null;
            try
            {
                enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDevice(deviceId);
                var previous = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(null);
                try
                {
                    capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previous);
                }

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

    public void Stop()
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        session?.Finish();
    }

    public void Dispose() => Stop();

    private sealed class Session(WasapiMicrophoneStream owner, MMDeviceEnumerator enumerator, MMDevice device, WasapiCapture capture)
    {
        private readonly StreamResampler _resampler = new(capture.WaveFormat.SampleRate);
        private readonly ManualResetEventSlim _stopped = new(false);
        private volatile bool _stopping;

        public void OnData(object? sender, WaveInEventArgs e)
        {
            if (_stopping)
            {
                return;
            }

            var block = SampleDecoder.ToMonoFloats(e.Buffer, e.BytesRecorded, capture.WaveFormat);
            owner.FramesCaptured?.Invoke(owner, new CapturedFrames(_resampler.Push(block), AudioMath.Rms(block)));
        }

        public void OnStopped(object? sender, StoppedEventArgs e)
        {
            _stopped.Set();
            if (e.Exception is not null && !_stopping)
            {
                owner.Failed?.Invoke(owner, new MicrophoneException(MicrophoneFailure.Classify(e.Exception), e.Exception.Message, e.Exception));
            }
        }

        public void Finish()
        {
            _stopping = true;
            try
            {
                capture.StopRecording();
            }
            catch (Exception)
            {
                _stopped.Set(); // the device may be gone already
            }

            if (_stopped.Wait(StopTimeout))
            {
                Release();
            }
            else
            {
                _ = Task.Run(Release); // disposing joins the capture thread: never on the caller
            }
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
