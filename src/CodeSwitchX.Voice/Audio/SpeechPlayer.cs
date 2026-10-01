using CodeSwitchX.Voice.Speech;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace CodeSwitchX.Voice.Audio;

/// <summary>Plays speech as it comes, chunk after chunk, on the default output device.</summary>
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
/// silence once they run out, until <see cref="Stop"/> closes it. Opened on the default device of the moment, so speech
/// follows a headset plugged in since the last reply.
/// </summary>
public sealed class WaveOutSpeechPlayer : ISpeechPlayer
{
    private readonly ILogger<WaveOutSpeechPlayer> _logger;
    private readonly Lock _lock = new();
    private WaveOutEvent? _output;
    private BufferedWaveProvider? _buffer;

    public WaveOutSpeechPlayer(ILogger<WaveOutSpeechPlayer> logger)
    {
        _logger = logger;
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
                var format = new WaveFormat(chunk.SampleRate, 16, 1);
                _buffer = new BufferedWaveProvider(format, TimeSpan.FromMinutes(5)) // a reply never fills it
                {
                    DiscardOnBufferOverflow = true,
                    ReadFully = true,
                };
                var output = new WaveOutEvent { DesiredLatency = 120 };
                output.PlaybackStopped += OnPlaybackStopped;
                output.Init(new Meter(_buffer, this));
                output.Play();
                _output = output;
            }

            var bytes = chunk.Pcm16.ToArray();
            _buffer.AddSamples(bytes, 0, bytes.Length);
        }
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
            if (ReferenceEquals(sender, _output))
            {
                StopLocked();
            }
        }
    }

    public void Dispose() => Stop();

    /// <summary>Passes the audio through and measures it on the way: the orb follows what is heard.</summary>
    private sealed class Meter(IWaveProvider source, WaveOutSpeechPlayer owner) : IWaveProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            double sum = 0;
            var samples = read / 2;
            for (var i = 0; i < samples; i++)
            {
                var sample = BitConverter.ToInt16(buffer, offset + i * 2) / 32768.0;
                sum += sample * sample;
            }

            owner.LevelChanged?.Invoke(owner, samples == 0 ? 0 : (float)Math.Sqrt(sum / samples));
            return read;
        }
    }
}
