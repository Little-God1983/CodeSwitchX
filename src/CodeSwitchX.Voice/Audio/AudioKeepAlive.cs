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
    /// How often it tries again when it does not play where it should (#184): on the Windows default in place of the output
    /// chosen, which an app that held it in exclusive mode lets go of without any device change to tell; or nowhere, after
    /// no output would start.
    /// </summary>
    internal static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(30);

    private readonly ILogger<AudioKeepAlive> _logger;
    private readonly IAudioOutput _audioOutput;
    private readonly TimeProvider _time;
    private readonly bool _watchDevices;
    private readonly ITimer _retry;
    private readonly EventHandler _onChosen;
    private readonly Lock _lock = new();
    private MMDeviceEnumerator? _enumerator;
    private DeviceChangeListener? _listener;
    private IWavePlayer? _output;
    private bool _inPlaceOfChosen; // _output is the Windows default, standing in for the output chosen
    private bool _shouldRun;
    private bool _disposed;
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
        _retry = time.CreateTimer(_ => Retry(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _onChosen = (_, _) => RestartSoon(); // another output chosen: keep that one awake instead
        _audioOutput.Changed += _onChosen;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return; // a start that came in during shutdown (the reply voice starts it on the thread pool)
            }

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

            if (_output is null)
            {
                SwitchLocked(onlyToChosen: false);
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _shouldRun = false;
            if (!_disposed)
            {
                StopStreamLocked();
            }
        }
    }

    public void Dispose()
    {
        _audioOutput.Changed -= _onChosen;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _shouldRun = false;
            StopStreamLocked();
            _disposed = true;
            _retry.Dispose(); // under the lock: no start or stop can reach it after this
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

    /// <summary>
    /// Plays silence on the output chosen, or the Windows default in its place; the one before stops only once the next
    /// plays, so a start that fails leaves what played on, and tries again in <see cref="RetryEvery"/>. With
    /// <paramref name="onlyToChosen"/> it moves only to the output chosen: the default it would fall back to plays already.
    /// </summary>
    private void SwitchLocked(bool onlyToChosen)
    {
        var wanted = _audioOutput.DeviceId; // read once: a pick meanwhile switches again anyway
        IWavePlayer? next = null;
        string? playsOn;
        try
        {
            next = _audioOutput.Open(Silence(), 300, out playsOn);
            if (onlyToChosen && playsOn is null)
            {
                AudioOutput.DisposeQuietly(next); // still held: the default it fell back to again
                _retry.Change(RetryEvery, Timeout.InfiniteTimeSpan);
                return;
            }

            Play(next);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio keep-alive could not start an output; it tries again in {Seconds} s", RetryEvery.TotalSeconds);
            AudioOutput.DisposeQuietly(next);
            _retry.Change(RetryEvery, Timeout.InfiniteTimeSpan);
            return;
        }

        StopStreamLocked();
        _output = next;
        _inPlaceOfChosen = wanted is not null && playsOn is null;
        if (_inPlaceOfChosen)
        {
            _retry.Change(RetryEvery, Timeout.InfiniteTimeSpan); // on the default in place of the output chosen
            _logger.LogInformation("Audio keep-alive keeps the Windows default awake in place of the output chosen");
        }
        else
        {
            _logger.LogInformation("Audio keep-alive started");
        }
    }

    /// <summary>Plays <paramref name="output"/>, listening for its end; one that will not play is let go unheard: no restart from it.</summary>
    private void Play(IWavePlayer output)
    {
        output.PlaybackStopped += OnPlaybackStopped;
        try
        {
            output.Play();
        }
        catch
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            throw;
        }
    }

    /// <summary>
    /// On the default in place of the output chosen: moves there once it opens. Otherwise (nothing plays, or a switch failed
    /// and an output no longer chosen plays on): starts on whatever opens now.
    /// </summary>
    private void Retry()
    {
        lock (_lock)
        {
            if (_shouldRun)
            {
                SwitchLocked(onlyToChosen: _output is not null && _inPlaceOfChosen);
            }
        }
    }

    /// <summary>The switch a pick starts after its debounce, at once.</summary>
    internal void SwitchNowForTests()
    {
        lock (_lock)
        {
            SwitchLocked(onlyToChosen: false);
        }
    }

    private void StopStreamLocked()
    {
        _retry.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
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

    /// <summary>
    /// The device went or failed: try again shortly on whatever is the default by then. On the thread pool: the default's
    /// WaveOutEvent raises it on its own playback thread, which must not wait for the lock a device open holds.
    /// </summary>
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e) => ThreadPool.QueueUserWorkItem(_ =>
    {
        lock (_lock)
        {
            if (!ReferenceEquals(sender, _output) || _disposed)
            {
                return; // one replaced meanwhile: the one playing now is fine
            }

            if (e.Exception is not null)
            {
                _logger.LogWarning(e.Exception, "Audio keep-alive stopped unexpectedly");
            }

            // The dead one is let go now: kept, a start that fails would leave it standing for one that plays.
            StopStreamLocked();
        }

        RestartSoon();
    });

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
                if (_shouldRun)
                {
                    SwitchLocked(onlyToChosen: false); // the one before plays until the next does
                }
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
