using System.Runtime.InteropServices;
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
    /// A player initialised with <paramref name="source"/>, not yet playing: on the device chosen while it is there and
    /// starts, on the Windows default otherwise (gone, not active, or held by another app in exclusive mode). Opened on the
    /// calling thread; throws only when the default fails too.
    /// </summary>
    IWavePlayer Open(IWaveProvider source, int latencyMs) => Open(source, latencyMs, out _);

    /// <summary>The same, saying whether it plays on the Windows default: none chosen, or the one chosen could not be used.</summary>
    IWavePlayer Open(IWaveProvider source, int latencyMs, out bool onDefault);
}

/// <summary>
/// The Windows default through <see cref="WaveOutEvent"/>, as before there was a choice: it opens on the default of the
/// moment. A device chosen plays through WASAPI in shared mode, which converts the format; the WinMM device numbers have
/// no stable tie to a device id.
/// </summary>
public sealed class AudioOutput : IAudioOutput
{
    private const int DeviceInUse = unchecked((int)0x8889000A); // AUDCLNT_E_DEVICE_IN_USE: held in exclusive mode

    private readonly ILogger<AudioOutput> _logger;
    private readonly Func<string, int, IWavePlayer?> _openChosen;
    private readonly Func<int, IWavePlayer> _openDefault;
    private string? _deviceId;

    public AudioOutput(ILogger<AudioOutput> logger)
        : this(logger, OpenWasapi, latencyMs => new WaveOutEvent { DesiredLatency = latencyMs })
    {
    }

    /// <summary>
    /// The players stand in for the devices in the tests; <paramref name="openChosen"/> gives null for a device not active
    /// and throws for one not there.
    /// </summary>
    internal AudioOutput(ILogger<AudioOutput> logger, Func<string, int, IWavePlayer?> openChosen, Func<int, IWavePlayer> openDefault)
    {
        _logger = logger;
        _openChosen = openChosen;
        _openDefault = openDefault;
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

    public IWavePlayer Open(IWaveProvider source, int latencyMs, out bool onDefault)
    {
        if (DeviceId is { } id && OpenChosen(id, source, latencyMs) is { } chosen)
        {
            onDefault = false;
            return chosen;
        }

        onDefault = true;
        return OpenDefault(source, latencyMs);
    }

    /// <summary>The device chosen, initialised; null when it cannot be used, which is logged: the default plays rather than nothing.</summary>
    private IWavePlayer? OpenChosen(string id, IWaveProvider source, int latencyMs)
    {
        IWavePlayer? chosen = null;
        try
        {
            chosen = _openChosen(id, latencyMs);
            if (chosen is null)
            {
                // A headset asleep or unplugged: an ordinary state, not worth a warning on every chime.
                _logger.LogInformation("The output chosen is not active; playing on the Windows default");
                return null;
            }

            chosen.Init(source); // where a device listed as active still refuses: AUDCLNT_E_DEVICE_IN_USE
            return chosen;
        }
        catch (COMException ex) when (ex.HResult == DeviceInUse)
        {
            // Lasts as long as the other app holds it: as ordinary as a device asleep.
            DisposeQuietly(chosen);
            _logger.LogInformation("The output chosen is held by another app; playing on the Windows default");
            return null;
        }
        catch (Exception ex)
        {
            // Unplugged since it was listed, or gone for good.
            DisposeQuietly(chosen);
            _logger.LogWarning(ex, "The output chosen could not be used; playing on the Windows default");
            return null;
        }
    }

    private IWavePlayer OpenDefault(IWaveProvider source, int latencyMs)
    {
        var player = _openDefault(latencyMs);
        try
        {
            player.Init(source);
            return player;
        }
        catch
        {
            DisposeQuietly(player);
            throw;
        }
    }

    /// <summary>The device chosen through WASAPI; null when it is listed but not active.</summary>
    private static IWavePlayer? OpenWasapi(string id, int latencyMs)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(id);
        if (device.State != DeviceState.Active)
        {
            device.Dispose();
            return null;
        }

        return new StoppedOffThread(new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latencyMs));
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
