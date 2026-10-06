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
/// Plays endless digital silence to keep the output Raven speaks on, and a Bluetooth audio link, awake, so speech does not
/// lose its first word to the device waking up. Follows the output chosen and default-device changes, and recovers from
/// device errors.
/// Best-effort throughout: it never throws. Ported from RAIVEN's <c>AudioKeepAlive</c>.
/// </summary>
public sealed class AudioKeepAlive : IAudioKeepAlive
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often a keep-alive on the Windows default in place of the output chosen tries that one again (#184): an app that
    /// held it in exclusive mode lets go without any device change to tell.
    /// </summary>
    internal static readonly TimeSpan RetryChosenEvery = TimeSpan.FromSeconds(30);

    private readonly ILogger<AudioKeepAlive> _logger;
    private readonly IAudioOutput _audioOutput;
    private readonly TimeProvider _time;
    private readonly bool _watchDevices;
    private readonly ITimer _retryChosen;
    private readonly Lock _lock = new();
    private MMDeviceEnumerator? _enumerator;
    private DeviceChangeListener? _listener;
    private IWavePlayer? _output;
    private bool _inPlaceOfChosen; // on the Windows default because the output chosen could not be used
    private bool _shouldRun;
    private int _restartPending;

    public AudioKeepAlive(IAudioOutput audioOutput, ILogger<AudioKeepAlive> logger)
        : this(audioOutput, logger, TimeProvider.System, watchDevices: true)
    {
    }

    /// <param name="watchDevices">Whether Windows' device changes restart it; the tests have no devices to watch.</param>
    internal AudioKeepAlive(IAudioOutput audioOutput, ILogger<AudioKeepAlive> logger, TimeProvider time, bool watchDevices)
    {
        _audioOutput = audioOutput;
        _logger = logger;
        _time = time;
        _watchDevices = watchDevices;
        _retryChosen = time.CreateTimer(_ => TryChosenAgain(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _audioOutput.Changed += (_, _) => RestartSoon(); // another output chosen: keep that one awake instead
    }

    public void Start()
    {
        lock (_lock)
        {
            _shouldRun = true;
            if (_enumerator is null && _watchDevices)
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
        _retryChosen.Dispose();
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

    private static SilenceProvider Silence() => new(new WaveFormat(44100, 16, 2));

    private void StartStreamLocked()
    {
        if (!_shouldRun || _output is not null)
        {
            return;
        }

        IWavePlayer? output = null;
        try
        {
            output = _audioOutput.Open(Silence(), 300, out var playsOn);
            PlayLocked(output, inPlaceOfChosen: playsOn is null && _audioOutput.DeviceId is not null);
            _logger.LogInformation("Audio keep-alive started");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio keep-alive could not start; it tries again on the next device change");
            DisposeQuietly(output);
            _output = null;
        }
    }

    private void PlayLocked(IWavePlayer output, bool inPlaceOfChosen)
    {
        output.PlaybackStopped += OnPlaybackStopped;
        output.Play();
        _output = output;
        _inPlaceOfChosen = inPlaceOfChosen;
        _retryChosen.Change(inPlaceOfChosen ? RetryChosenEvery : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// On the Windows default in place of the output chosen: tries that one again, and moves there once it opens. The
    /// default plays on meanwhile, so a try that fails again leaves no gap.
    /// </summary>
    private void TryChosenAgain()
    {
        lock (_lock)
        {
            if (!_shouldRun || _output is null || !_inPlaceOfChosen)
            {
                return;
            }

            IWavePlayer? chosen = null;
            try
            {
                chosen = _audioOutput.Open(Silence(), 300, out var playsOn);
                if (playsOn is null)
                {
                    DisposeQuietly(chosen); // still held: the default it fell back to again
                    _retryChosen.Change(RetryChosenEvery, Timeout.InfiniteTimeSpan);
                    return;
                }

                StopStreamLocked();
                PlayLocked(chosen, inPlaceOfChosen: false);
                _logger.LogInformation("Audio keep-alive is back on the output chosen");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audio keep-alive could not try the output chosen again");
                DisposeQuietly(chosen);
                _retryChosen.Change(RetryChosenEvery, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void StopStreamLocked()
    {
        _retryChosen.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _inPlaceOfChosen = false;
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

    private static void DisposeQuietly(IWavePlayer? player)
    {
        try
        {
            player?.Dispose();
        }
        catch
        {
            // Best effort: a player that half started.
        }
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
            await Task.Delay(RestartDelay, _time).ConfigureAwait(false);
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
