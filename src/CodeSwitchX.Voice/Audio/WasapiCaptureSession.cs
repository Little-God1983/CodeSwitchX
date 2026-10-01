using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CodeSwitchX.Voice.Audio;

/// <summary>
/// One open WASAPI microphone, from <see cref="Open"/> to <see cref="Finish"/>: the opening and closing that
/// <see cref="WasapiMicrophoneRecorder"/> and Open mic's stream share, so a fix to their workarounds lands in both.
/// <para>
/// Every session gets its own device enumerator, made on the calling thread (callers start from the thread pool) and
/// released with the session: one made on the UI thread that DI builds on could marshal calls from the pool back to that
/// thread, or fail to marshal at all.
/// </para>
/// </summary>
internal sealed class WasapiCaptureSession
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    private readonly Action<MicrophoneException> _failed;
    private readonly ManualResetEventSlim _stopped = new(false);
    private Action<float[]> _data = _ => { };
    private volatile bool _finishing;

    private WasapiCaptureSession(MMDeviceEnumerator enumerator, MMDevice device, WasapiCapture capture, Action<MicrophoneException> failed)
    {
        _enumerator = enumerator;
        _device = device;
        _capture = capture;
        _failed = failed;
    }

    /// <summary>The device's own rate: the blocks come at it.</summary>
    public int SampleRate => _capture.WaveFormat.SampleRate;

    /// <summary>
    /// Opens <paramref name="deviceId"/> and starts recording. <paramref name="attach"/> sees the session before the
    /// first block and returns what takes each block (mono floats at <see cref="SampleRate"/>, on the capture thread).
    /// <paramref name="failed"/> hears of a device that dies while recording, never of one stopped by <see cref="Finish"/>.
    /// </summary>
    /// <exception cref="MicrophoneException">The device could not be opened; nothing is left open.</exception>
    public static WasapiCaptureSession Open(string deviceId, Func<WasapiCaptureSession, Action<float[]>> attach, Action<MicrophoneException> failed)
    {
        MMDeviceEnumerator? enumerator = null;
        MMDevice? device = null;
        WasapiCapture? capture = null;
        WasapiCaptureSession? session = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(deviceId);
            capture = CreateCapture(device);
            session = new WasapiCaptureSession(enumerator, device, capture, failed);
            session._data = attach(session);
            capture.DataAvailable += session.OnData;
            capture.RecordingStopped += session.OnStopped;
            capture.StartRecording();
            return session;
        }
        catch (Exception e)
        {
            capture?.Dispose();
            session?._stopped.Dispose(); // after the capture: its thread may still have set it
            device?.Dispose();
            enumerator?.Dispose();
            throw new MicrophoneException(MicrophoneFailure.Classify(e), e.Message, e);
        }
    }

    /// <summary>Stops the device from the capture thread (the recorder's length limit); <see cref="Finish"/> still closes it.</summary>
    public void StopRecording() => _capture.StopRecording();

    /// <summary>Stops and closes the device. Blocks no longer go out once it is called. Waits up to two seconds for the
    /// capture thread; past that, the device is released off the caller's thread, since disposing joins that thread.</summary>
    public void Finish()
    {
        _finishing = true;
        try
        {
            _capture.StopRecording();
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
            _ = Task.Run(Release);
        }
    }

    // WasapiCapture remembers SynchronizationContext.Current and posts RecordingStopped through it. Built on the UI thread
    // that would queue the event behind a Finish() that is waiting for it, so build it with no context: every event then
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

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_finishing)
        {
            return;
        }

        _data(SampleDecoder.ToMonoFloats(e.Buffer, e.BytesRecorded, _capture.WaveFormat));
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        _stopped.Set();
        if (e.Exception is not null && !_finishing)
        {
            _failed(new MicrophoneException(MicrophoneFailure.Classify(e.Exception), e.Exception.Message, e.Exception));
        }
    }

    private void Release()
    {
        _capture.DataAvailable -= OnData;
        _capture.RecordingStopped -= OnStopped;
        _capture.Dispose();
        _device.Dispose();
        _enumerator.Dispose();
        _stopped.Dispose();
    }
}
