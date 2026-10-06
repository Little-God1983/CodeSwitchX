using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CodeSwitchX.Voice.Audio;

/// <summary>Where Raven's sound goes: its speech, the chat chime, the voice samples and the keep-alive.</summary>
public interface IAudioOutput
{
    /// <summary>The output device chosen, by its id; null follows the Windows default. Any thread.</summary>
    string? DeviceId { get; set; }

    /// <summary>Raised on the setting thread when <see cref="DeviceId"/> changes: what holds an output open moves to the new one.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// A player not yet initialised, on the device chosen while it is there, on the Windows default otherwise. Opened on
    /// the calling thread, which should then Init it.
    /// </summary>
    IWavePlayer Create(int latencyMs);
}

/// <summary>
/// The Windows default through <see cref="WaveOutEvent"/>, as before there was a choice: it opens on the default of the
/// moment. A device chosen plays through WASAPI in shared mode, which converts the format; the WinMM device numbers have
/// no stable tie to a device id.
/// </summary>
public sealed class AudioOutput : IAudioOutput
{
    private readonly ILogger<AudioOutput> _logger;
    private string? _deviceId;

    public AudioOutput(ILogger<AudioOutput> logger)
    {
        _logger = logger;
    }

    public event EventHandler? Changed;

    public string? DeviceId
    {
        get => Volatile.Read(ref _deviceId);
        set
        {
            if (Interlocked.Exchange(ref _deviceId, value) != value)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public IWavePlayer Create(int latencyMs)
    {
        if (DeviceId is { } id)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(id);
                if (device.State == DeviceState.Active)
                {
                    return new StoppedOffThread(new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latencyMs));
                }

                device.Dispose();
                _logger.LogInformation("The output chosen is not active; playing on the Windows default");
            }
            catch (Exception ex)
            {
                // Unplugged since it was listed, or gone for good: the default plays rather than nothing.
                _logger.LogWarning(ex, "The output chosen could not be opened; playing on the Windows default");
            }
        }

        return new WaveOutEvent { DesiredLatency = latencyMs };
    }
}

/// <summary>
/// Raises the player's <see cref="IWavePlayer.PlaybackStopped"/> on the thread pool. WasapiOut raises it on its own play
/// thread, and after a device error (unplugged, a Bluetooth link gone) still reports Playing: a handler that stops it there
/// joins that very thread and never returns. WaveOutEvent sets Stopped first, so the callers were written for that.
/// </summary>
internal sealed class StoppedOffThread : IWavePlayer
{
    private readonly IWavePlayer _inner;

    public StoppedOffThread(IWavePlayer inner)
    {
        _inner = inner;
        _inner.PlaybackStopped += (_, e) => ThreadPool.QueueUserWorkItem(_ => PlaybackStopped?.Invoke(this, e));
    }

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public float Volume
    {
        get => _inner.Volume;
        set => _inner.Volume = value;
    }

    public PlaybackState PlaybackState => _inner.PlaybackState;

    public WaveFormat OutputWaveFormat => _inner.OutputWaveFormat;

    public void Init(IWaveProvider waveProvider) => _inner.Init(waveProvider);

    public void Play() => _inner.Play();

    public void Pause() => _inner.Pause();

    public void Stop() => _inner.Stop();

    public void Dispose() => _inner.Dispose();
}
