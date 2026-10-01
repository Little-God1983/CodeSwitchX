using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CodeSwitchX.Voice.Audio;

/// <summary>Keeps the output device awake while it is on; the tests use a fake.</summary>
public interface IAudioKeepAlive : IDisposable
{
    /// <summary>Starts, or keeps, it on. Never throws.</summary>
    void Start();

    /// <summary>Lets the device sleep again. Never throws.</summary>
    void Stop();
}

/// <summary>
/// Plays endless digital silence to keep the default output device, and a Bluetooth audio link, awake, so speech does not
/// lose its first word to the device waking up. Follows default-device changes and recovers from device errors.
/// Best-effort throughout: it never throws. Ported from RAIVEN's <c>AudioKeepAlive</c>.
/// </summary>
public sealed class AudioKeepAlive : IAudioKeepAlive
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

    private readonly ILogger<AudioKeepAlive> _logger;
    private readonly Lock _lock = new();
    private MMDeviceEnumerator? _enumerator;
    private DeviceChangeListener? _listener;
    private WaveOutEvent? _output;
    private bool _shouldRun;
    private int _restartPending;

    public AudioKeepAlive(ILogger<AudioKeepAlive> logger)
    {
        _logger = logger;
    }

    public void Start()
    {
        lock (_lock)
        {
            _shouldRun = true;
            if (_enumerator is null)
            {
                try
                {
                    _enumerator = new MMDeviceEnumerator();
                    _listener = new DeviceChangeListener(this);
                    _enumerator.RegisterEndpointNotificationCallback(_listener);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Audio keep-alive: no device-change watcher; going on without it");
                }
            }

            StartStreamLocked();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _shouldRun = false;
            StopStreamLocked();
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_lock)
        {
            if (_enumerator is not null && _listener is not null)
            {
                try
                {
                    _enumerator.UnregisterEndpointNotificationCallback(_listener);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Audio keep-alive: unregistering the device-change watcher failed");
                }
            }

            _enumerator?.Dispose();
            _enumerator = null;
            _listener = null;
        }
    }

    private void StartStreamLocked()
    {
        if (!_shouldRun || _output is not null)
        {
            return;
        }

        WaveOutEvent? output = null;
        try
        {
            output = new WaveOutEvent();
            output.Init(new SilenceProvider(new WaveFormat(44100, 16, 2)));
            output.PlaybackStopped += OnPlaybackStopped;
            output.Play();
            _output = output;
            _logger.LogInformation("Audio keep-alive started");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio keep-alive could not start; it tries again on the next device change");
            try
            {
                output?.Dispose();
            }
            catch
            {
                // Best effort: a player that half started.
            }

            _output = null;
        }
    }

    private void StopStreamLocked()
    {
        if (_output is null)
        {
            return;
        }

        try
        {
            _output.PlaybackStopped -= OnPlaybackStopped; // a stop on purpose must not restart it
            _output.Stop();
            _output.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio keep-alive: stopping failed");
        }

        _output = null;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // The device went or failed: try again shortly on whatever is the default by then.
        if (e.Exception is not null)
        {
            _logger.LogWarning(e.Exception, "Audio keep-alive stopped unexpectedly");
        }

        RestartSoon();
    }

    /// <summary>Debounced: the default-device change comes once per role, and errors come in bursts.</summary>
    private void RestartSoon()
    {
        if (Interlocked.Exchange(ref _restartPending, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(RestartDelay).ConfigureAwait(false);
            Interlocked.Exchange(ref _restartPending, 0);
            lock (_lock)
            {
                if (!_shouldRun)
                {
                    return;
                }

                StopStreamLocked();
                StartStreamLocked();
            }
        });
    }

    private sealed class DeviceChangeListener(AudioKeepAlive owner) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            // WaveOutEvent binds its device at Init and never moves: start again on the new default, so the keep-alive
            // follows a headset connected meanwhile.
            if (flow == DataFlow.Render && role == Role.Multimedia)
            {
                owner.RestartSoon();
            }
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
        }

        public void OnDeviceAdded(string pwstrDeviceId)
        {
        }

        public void OnDeviceRemoved(string deviceId)
        {
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
        }
    }
}
