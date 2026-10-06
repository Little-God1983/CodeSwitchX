using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using CodeSwitchX.Voice.Speech;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace CodeSwitchX.Voice.Audio;

/// <summary>Plays speech as it comes, chunk after chunk, on the output chosen (<see cref="IAudioOutput"/>).</summary>
public interface ISpeechPlayer : IDisposable
{
    /// <summary>
    /// Plays the chunk after those queued before it; the first one opens the device, and throws when none opens. Any thread.
    /// </summary>
    /// <param name="hushed">
    /// Whether the reply is hushed: a device that finishes opening after that is let go unheard, so a hush that waits for
    /// this call to return (the reply voice's gate) is not heard on it a moment first (#186).
    /// </param>
    void Enqueue(SpeechChunk chunk, Func<bool>? hushed = null);

    /// <summary>
    /// How much of the queued audio is still to be heard; zero when it is done. Any thread, and never waits: the UI thread's
    /// Hush asks it while a device may be opening (#177).
    /// </summary>
    TimeSpan Remaining { get; }

    /// <summary>
    /// Drops whatever is queued and closes the device playing it. Any thread, and never waits for a device still opening:
    /// that one is closed unheard once its open returns, on the thread that opens it.
    /// </summary>
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
    private readonly Lock _lock = new(); // the state only: no device is opened or closed under it (#186)
    private IWavePlayer? _output;
    private volatile BufferedWaveProvider? _buffer; // read without the lock by Remaining: Hush asks it on the UI thread
    private string? _playsOn; // the device it plays on, null for the Windows default: a move to where it plays already is none
    private bool _reopened; // reopened after the output failed mid-reply: a second failure drops what is queued
    private int _generation; // a stop, a new queue, a move or a reopen: a device opened for an older one is let go unheard
    private string? _openingFor; // the output chosen as the device began to open: a move there is none
    private Func<bool>? _hushed; // whether the reply queued last is hushed: a device opening for its queue is let go then
    private int _movePending; // a move queued and not yet started: picks in a row (arrowing through the list) queue one
    private readonly Action<Action> _offThread;

    public WaveOutSpeechPlayer(IAudioOutput audioOutput, ILogger<WaveOutSpeechPlayer> logger)
        : this(audioOutput, logger, move => ThreadPool.QueueUserWorkItem(_ => move()))
    {
    }

    /// <param name="offThread">
    /// Runs a move away from the thread that picked the output, the UI thread: opening a device takes tens of milliseconds,
    /// a waking Bluetooth one longer (#177). Inline in the tests.
    /// </param>
    internal WaveOutSpeechPlayer(IAudioOutput audioOutput, ILogger<WaveOutSpeechPlayer> logger, Action<Action> offThread)
    {
        _audioOutput = audioOutput;
        _logger = logger;
        _offThread = offThread;
        _audioOutput.Changed += (_, _) => MoveSoon();
    }

    private void MoveSoon()
    {
        if (Interlocked.Exchange(ref _movePending, 1) == 0)
        {
            _offThread(Move);
        }
    }

    public event EventHandler<float>? LevelChanged;

    /// <summary>Without the lock: <see cref="ReplyVoice.Hush"/> asks it on the UI thread and returns at once.</summary>
    public TimeSpan Remaining => _buffer?.BufferedDuration ?? TimeSpan.Zero;

    /// <summary>
    /// Queues the chunk; the first of a reply (or of another sample rate) opens the device, on this thread and outside the
    /// lock: a stop or a move meanwhile never waits for it, and the chunks after it queue at once.
    /// </summary>
    public void Enqueue(SpeechChunk chunk, Func<bool>? hushed = null)
    {
        IWavePlayer? old = null;
        BufferedWaveProvider? toOpen = null;
        var generation = 0;
        lock (_lock)
        {
            if (_buffer is not null && _buffer.WaveFormat.SampleRate != chunk.SampleRate)
            {
                old = DetachLocked();
                _buffer = null;
            }

            if (_buffer is null)
            {
                _buffer = toOpen = NewBuffer(chunk.SampleRate);
                generation = NextGenerationLocked();
            }

            _hushed = hushed; // a move or a reopen of this queue asks it too

            // The chunk's own array where it has one (it always does from the sidecar): no second copy per chunk.
            var segment = MemoryMarshal.TryGetArray(chunk.Pcm16, out var array) ? array : new ArraySegment<byte>(chunk.Pcm16.ToArray());
            _buffer.AddSamples(segment.Array!, segment.Offset, segment.Count);
        }

        Retire(old);
        if (toOpen is not null)
        {
            OpenFor(toOpen, generation, afterFailure: false, throwOnFailure: true);
        }
    }

    private static BufferedWaveProvider NewBuffer(int sampleRate) =>
        new(new WaveFormat(sampleRate, 16, 1), TimeSpan.FromMinutes(5)) // a reply never fills it
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };

    /// <summary>
    /// Another output chosen while speech plays (a pick on the panel mid-sentence): what is still queued goes on there,
    /// rather than the rest of the reply on the output left. A buffer of the old device's, a tenth of a second, is lost.
    /// A pick while the first device still opens moves it too: that one is let go unheard. On the thread pool: nothing
    /// it throws may end the app.
    /// </summary>
    private void Move()
    {
        Volatile.Write(ref _movePending, 0); // a pick from here on queues another move, which reads the choice anew
        try
        {
            IWavePlayer? old;
            BufferedWaveProvider buffer;
            int generation;
            lock (_lock)
            {
                // Plays where the choice points already: two picks in a row (the first move ran after both), or the choice
                // gone back to the Windows default that speech fell back to (the headset left the list).
                if (_buffer is not { } queued
                    || (_output is not null && _audioOutput.DeviceId == _playsOn)
                    || (Opening && _audioOutput.DeviceId == _openingFor))
                {
                    return; // or opening there already: a waking headset is not woken twice
                }

                old = DetachLocked(); // the queue stays: Remaining reads on through the move
                buffer = queued;
                generation = NextGenerationLocked();
            }

            Retire(old); // the orb rests while the new device wakes
            OpenFor(buffer, generation, afterFailure: false, throwOnFailure: false); // on the default when the one chosen fails
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Moving the speech to the output chosen failed");
        }
    }

    /// <summary>
    /// Opens a device for <paramref name="buffer"/>, outside the lock, and plays it there unless a stop, another queue, a move
    /// or a reopen came meanwhile: then it is let go unheard. One that fails (none there, or busy) drops the queue, so the
    /// next chunk tries again instead of filling a buffer nothing plays.
    /// </summary>
    private void OpenFor(BufferedWaveProvider buffer, int generation, bool afterFailure, bool throwOnFailure)
    {
        lock (_lock)
        {
            if (generation == _generation && IsHushedLocked())
            {
                StopLocked(); // hushed before it opened: no device is woken for it
            }

            if (generation != _generation)
            {
                return; // stopped, replaced or moved before it opened
            }

            _openingFor = _audioOutput.DeviceId; // as late as can be: Open reads it once more
        }

        IWavePlayer output;
        string? playsOn;
        try
        {
            output = _audioOutput.Open(new Meter(buffer, this), 120, out playsOn);
        }
        catch (Exception ex)
        {
            Fail(generation, ex, throwOnFailure);
            return;
        }

        Exception? wontPlay = null;
        bool played;
        lock (_lock)
        {
            if (generation == _generation && IsHushedLocked())
            {
                StopLocked(); // hushed while it opened: the stop waiting for the caller would come after it played a moment
            }

            played = generation == _generation;
            if (played)
            {
                output.PlaybackStopped += OnPlaybackStopped;
                try
                {
                    output.Play();
                    _output = output;
                    _playsOn = playsOn;
                    _reopened = afterFailure;
                }
                catch (Exception ex)
                {
                    output.PlaybackStopped -= OnPlaybackStopped;
                    played = false;
                    wontPlay = ex;
                }
            }
        }

        if (!played)
        {
            Close(output); // stopped, replaced or moved while it opened: never heard
            if (wontPlay is not null)
            {
                Fail(generation, wontPlay, throwOnFailure);
            }

            return;
        }

        if (afterFailure)
        {
            _logger.LogInformation("Speech goes on on {Output}", playsOn is null ? "the Windows default" : "the output chosen");
        }

        if (playsOn is not null && playsOn != _audioOutput.DeviceId)
        {
            MoveSoon(); // picked away and back while it opened (one move for both): it opened on the one in between
        }
    }

    /// <summary>
    /// The device for <paramref name="generation"/> would not open or play: nothing plays the queue, which is dropped, so
    /// the next chunk tries again. Unless a move or a reopen took the queue over meanwhile: that one plays it.
    /// </summary>
    private void Fail(int generation, Exception error, bool rethrow)
    {
        lock (_lock)
        {
            if (generation != _generation)
            {
                return;
            }

            rethrow &= !IsHushedLocked(); // the reply is silenced on purpose: no "could not speak" for it
            StopLocked();
        }

        RestTheOrb();
        if (rethrow)
        {
            ExceptionDispatchInfo.Throw(error);
        }

        _logger.LogWarning(error, "Speech found no output, nor the Windows default; what was queued is dropped, the next chunk tries again");
    }

    public void Stop()
    {
        IWavePlayer? old;
        lock (_lock)
        {
            old = StopLocked();
        }

        if (old is not null)
        {
            Close(old);
        }

        RestTheOrb();
    }

    /// <summary>A device is opening for the queue: none plays it yet.</summary>
    private bool Opening => _buffer is not null && _output is null;

    /// <summary>The caller's answer to whether its reply is hushed; one that throws is no hush, and is logged.</summary>
    private bool IsHushedLocked()
    {
        try
        {
            return _hushed?.Invoke() == true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Asking whether the reply was hushed failed");
            return false;
        }
    }

    /// <summary>
    /// Drops the queue, emptied first: a device another thread still closes (a move, a reopen) plays silence from here on.
    /// A device opening now is let go unheard. Returns the one that played, to close outside the lock.
    /// </summary>
    private IWavePlayer? StopLocked()
    {
        var old = DetachLocked();
        _buffer?.ClearBuffer();
        _buffer = null;
        _hushed = null; // the reply's check goes with its queue
        NextGenerationLocked();
        return old;
    }

    private int NextGenerationLocked() => ++_generation;

    /// <summary>Takes the device out of play, to be closed outside the lock; what is queued stays, for a move or a reopen.</summary>
    private IWavePlayer? DetachLocked()
    {
        var output = _output;
        if (output is not null)
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            _output = null;
        }

        return output;
    }

    /// <summary>Level 0 to the orb. A listener that fails is logged: it must not end a move or a reopen, which hold the speech.</summary>
    private void RestTheOrb()
    {
        try
        {
            LevelChanged?.Invoke(this, 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A speech level listener failed");
        }
    }

    /// <summary>Lets go of a device taken out of play, if any, and rests the orb once it is quiet.</summary>
    private void Retire(IWavePlayer? output)
    {
        if (output is not null)
        {
            Close(output);
            RestTheOrb();
        }
    }

    /// <summary>Lets go of a device taken out of play.</summary>
    private void Close(IWavePlayer output)
    {
        try
        {
            output.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stopping the speech output failed");
        }

        try
        {
            output.Dispose(); // also after a failed stop: the device is let go either way
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Closing the speech output failed");
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Only a device error ends playback before Stop: the device went (a headset unplugged mid-sentence).
        if (e.Exception is not null)
        {
            _logger.LogWarning(e.Exception, "Speech playback stopped");
        }

        IWavePlayer? dead;
        BufferedWaveProvider? buffer = null;
        var generation = 0;
        lock (_lock)
        {
            // An output replaced meanwhile (a new sample rate, the next reply) is not this one: it plays on.
            if (!ReferenceEquals(sender, _output))
            {
                return;
            }

            dead = DetachLocked();
            // Speech comes faster than it plays, so seconds of the reply can still be queued: they go on, once, on the output
            // chosen if it is still there (a driver reset), on the Windows default if not (unplugged). Only what the failed
            // device held, a tenth of a second, is lost.
            if (e.Exception is not null && !_reopened && _buffer is { BufferedBytes: > 0 } queued)
            {
                buffer = queued;
                generation = NextGenerationLocked();
            }
            else
            {
                StopLocked();
            }
        }

        Retire(dead); // the orb rests while the other device opens
        if (buffer is not null)
        {
            OpenFor(buffer, generation, afterFailure: true, throwOnFailure: false);
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
