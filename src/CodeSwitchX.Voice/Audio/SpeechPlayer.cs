using System.Runtime.InteropServices;
using CodeSwitchX.Voice.Speech;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace CodeSwitchX.Voice.Audio;

/// <summary>Plays speech as it comes, chunk after chunk, on the output chosen (<see cref="IAudioOutput"/>).</summary>
public interface ISpeechPlayer : IDisposable
{
    /// <summary>Plays the chunk after those queued before it; the first one opens the device. Any thread.</summary>
    void Enqueue(SpeechChunk chunk);

    /// <summary>How much of the queued audio is still to be heard; zero when it is done.</summary>
    TimeSpan Remaining { get; }

    /// <summary>Drops whatever is queued and closes the device at once. Any thread.</summary>
    void Stop();

    /// <summary>How loud what plays now is, 0..1, about twenty times a second while it plays; on the playback thread.</summary>
    event EventHandler<float>? LevelChanged;
}

/// <summary>
/// NAudio's <see cref="WaveOutEvent"/> fed from a <see cref="BufferedWaveProvider"/>: chunks queue there, and it plays
/// silence once they run out, until <see cref="Stop"/> closes it. Opened on the output chosen, or on the default device of
/// the moment, so speech follows a headset plugged in since the last reply.
/// </summary>
public sealed class WaveOutSpeechPlayer : ISpeechPlayer
{
    private readonly ILogger<WaveOutSpeechPlayer> _logger;
    private readonly IAudioOutput _audioOutput;
    private readonly Lock _lock = new();
    private IWavePlayer? _output;
    private BufferedWaveProvider? _buffer;
    private bool _onDefault; // reopened on the Windows default after the output failed mid-reply: a second failure ends it

    public WaveOutSpeechPlayer(IAudioOutput audioOutput, ILogger<WaveOutSpeechPlayer> logger)
    {
        _audioOutput = audioOutput;
        _logger = logger;
        _audioOutput.Changed += (_, _) => Move();
    }

    public event EventHandler<float>? LevelChanged;

    public TimeSpan Remaining
    {
        get
        {
            lock (_lock)
            {
                return _buffer?.BufferedDuration ?? TimeSpan.Zero;
            }
        }
    }

    public void Enqueue(SpeechChunk chunk)
    {
        lock (_lock)
        {
            if (_buffer is not null && _buffer.WaveFormat.SampleRate != chunk.SampleRate)
            {
                StopLocked();
            }

            if (_buffer is null)
            {
                Open(chunk.SampleRate);
            }

            // The chunk's own array where it has one (it always does from the sidecar): no second copy per chunk.
            var segment = MemoryMarshal.TryGetArray(chunk.Pcm16, out var array) ? array : new ArraySegment<byte>(chunk.Pcm16.ToArray());
            _buffer!.AddSamples(segment.Array!, segment.Offset, segment.Count);
        }
    }

    /// <summary>
    /// Another output chosen while speech plays (a pick on the panel mid-sentence): what is still queued goes on there,
    /// rather than the rest of the reply on the output left. A buffer of the old device's, a tenth of a second, is lost.
    /// </summary>
    private void Move()
    {
        lock (_lock)
        {
            if (_output is null || _buffer is null)
            {
                return;
            }

            var buffer = _buffer;
            StopLocked();
            try
            {
                Open(buffer); // on the default when the one chosen fails to start: the rest is heard there
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Speech could not move to the output chosen nor to the Windows default; the next chunk tries again");
            }
        }
    }

    /// <summary>
    /// Opens the output. Only a device that opened and plays is kept: one that fails (none there, or busy) is
    /// disposed and the error thrown, so the next chunk tries again instead of filling a buffer nothing plays.
    /// </summary>
    private void Open(int sampleRate)
    {
        Open(new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1), TimeSpan.FromMinutes(5)) // a reply never fills it
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        });
    }

    private void Open(BufferedWaveProvider buffer, bool onDefault = false)
    {
        var meter = new Meter(buffer, this);
        var output = onDefault ? _audioOutput.OpenDefault(meter, 120) : _audioOutput.Open(meter, 120);
        try
        {
            output.PlaybackStopped += OnPlaybackStopped;
            output.Play();
        }
        catch
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            output.Dispose();
            throw;
        }

        _buffer = buffer;
        _output = output;
        _onDefault = onDefault;
    }

    public void Stop()
    {
        lock (_lock)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            try
            {
                _output.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stopping the speech output failed");
            }

            _output.Dispose();
            _output = null;
        }

        _buffer = null;
        LevelChanged?.Invoke(this, 0);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Only a device error ends playback before Stop: the device went (a headset unplugged mid-sentence).
        if (e.Exception is not null)
        {
            _logger.LogWarning(e.Exception, "Speech playback stopped");
        }

        lock (_lock)
        {
            // An output replaced meanwhile (a new sample rate, the next reply) is not this one: it plays on.
            if (!ReferenceEquals(sender, _output))
            {
                return;
            }

            // Speech comes faster than it plays, so seconds of the reply can still be queued: they go on on the Windows
            // default, once. Only what the failed device held, a tenth of a second, is lost.
            var buffer = _buffer!;
            var reopen = e.Exception is not null && !_onDefault;
            StopLocked();
            if (!reopen)
            {
                return;
            }

            try
            {
                Open(buffer, onDefault: true);
                _logger.LogInformation("Speech goes on on the Windows default");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Speech could not go on on the Windows default either; the rest of the reply is dropped");
            }
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Passes the audio through and measures it on the way: the orb follows what is heard, about twenty times a second
    /// however often the output reads (WASAPI every 10 ms, WinMM every 50 or so).
    /// </summary>
    private sealed class Meter(IWaveProvider source, WaveOutSpeechPlayer owner) : IWaveProvider
    {
        private readonly int _window = source.WaveFormat.SampleRate / 20;
        private double _sum;
        private int _samples;

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            var samples = read / 2;
            for (var i = 0; i < samples; i++)
            {
                var sample = BitConverter.ToInt16(buffer, offset + i * 2) / 32768.0;
                _sum += sample * sample;
            }

            _samples += samples;
            if (_samples >= _window || samples == 0)
            {
                owner.LevelChanged?.Invoke(owner, _samples == 0 ? 0 : (float)Math.Sqrt(_sum / _samples));
                (_sum, _samples) = (0, 0);
            }

            return read;
        }
    }
}
