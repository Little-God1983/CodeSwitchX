using System.Threading.Channels;
using CodeSwitchX.Voice.Audio;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Voice.Speech;

/// <summary>
/// Speaks Raven's replies as they stream in: each reply's text is cut into sentences, and each sentence is spoken as soon
/// as it is whole, while the rest of the reply is still coming. At most <see cref="MaximumSentences"/> per reply are
/// spoken; the full text stays in the log. Replies are spoken in the order they began, one sentence at a time.
/// <see cref="Hush"/> (the user starts talking) stops at once, and what was begun before it is never spoken after it.
/// </summary>
public sealed class ReplyVoice : IDisposable
{
    /// <summary>A spoken reply stays short: the panel shows the rest.</summary>
    public const int MaximumSentences = 3;

    /// <summary>How long a sentence may take to its first audio: generation behind another sentence included.</summary>
    public static readonly TimeSpan FirstAudioTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long the audio of a sentence may pause between two chunks before the voice counts as hung.</summary>
    public static readonly TimeSpan ChunkTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long past its own length the queued audio may take to play out before the player counts as hung, and how
    /// long the device keeps playing after the buffer runs dry (what the output had already taken from it).
    /// </summary>
    public static readonly TimeSpan PlayOutMargin = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Tail = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// How long the output is kept awake after Raven last spoke: a follow-up question finds it awake. Not for good: a
    /// stream that never ends keeps Windows from going to sleep.
    /// </summary>
    public static readonly TimeSpan KeepAwake = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan PlayOutPoll = TimeSpan.FromMilliseconds(50);

    private readonly ITextToSpeech _tts;
    private readonly ISpeechPlayer _player;
    private readonly IAudioKeepAlive _keepAlive;
    private readonly ITimer _sleep;
    private readonly TimeProvider _time;
    private readonly ILogger<ReplyVoice> _logger;
    private readonly Channel<Sentence> _sentences = Channel.CreateUnbounded<Sentence>();
    private readonly Lock _lock = new();

    /// <summary>Taken while <see cref="_speaking"/> changes and the change is told, so the changes are told in order.</summary>
    private readonly Lock _telling = new();

    /// <summary>
    /// Every call to the player goes through this gate, never under <see cref="_lock"/>: the first chunk of a reply opens
    /// the output device, which takes a while on a Bluetooth headset, and a hush on the UI thread must not wait for it.
    /// </summary>
    private readonly SemaphoreSlim _playerGate = new(1, 1);
    private readonly Task _loop;

    /// <summary>Stops asked for by hushes, and how many of them the player has had (under <see cref="_playerGate"/>).</summary>
    private long _stopsAsked;
    private long _stopsDone;

    /// <summary>How many replies have begun: each one's number.</summary>
    private long _replies;

    /// <summary>Replies numbered up to this one are not spoken (any more): they began before the last hush.</summary>
    private long _hushedThrough;
    private CancellationTokenSource _hush = new();
    private volatile bool _muted;
    private volatile bool _disposed;

    /// <summary>Sentences queued and not yet spoken or dropped.</summary>
    private int _pending;

    /// <summary>Replies begun and not yet complete: the brain is still working on them.</summary>
    private int _open;
    private bool _speaking;

    public ReplyVoice(ITextToSpeech tts, ISpeechPlayer player, IAudioKeepAlive keepAlive, TimeProvider time, ILogger<ReplyVoice> logger)
    {
        _tts = tts;
        _player = player;
        _keepAlive = keepAlive;
        _sleep = time.CreateTimer(_ => LetSleep(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _time = time;
        _logger = logger;
        _player.LevelChanged += (_, level) => LevelChanged?.Invoke(this, level);
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Raised on any thread when speaking starts (the first audio is queued) and when it has played out or stopped.</summary>
    public event EventHandler<bool>? SpeakingChanged;

    /// <summary>The loudness of what plays, 0..1; on the playback thread.</summary>
    public event EventHandler<float>? LevelChanged;

    /// <summary>A reply is not spoken (or not to its end), and why, for the user; on any thread.</summary>
    public event EventHandler<string>? Unspoken;

    /// <summary>Muting hushes what is being said; replies begun while muted are never spoken.</summary>
    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            if (value)
            {
                Hush();
            }
        }
    }

    public bool IsSpeaking
    {
        get
        {
            lock (_lock)
            {
                return _speaking;
            }
        }
    }

    /// <summary>
    /// A reply is on its way (the user started their turn): wakes the output now, so a Bluetooth headset does not lose the
    /// first word to waking up. It sleeps again <see cref="KeepAwake"/> after the voice last went quiet and no reply is
    /// still being written. Returns at once on any thread: opening the device is done on the thread pool, off the mic
    /// press, and there no synchronisation context catches its events. Only for a voice that is ready or loading: one
    /// not installed, or failed, would keep a stream of silence open (Bluetooth busy, Windows awake) for nothing.
    /// </summary>
    public void Expect()
    {
        if (_muted || _tts.Status.State is not (TextToSpeechState.Ready or TextToSpeechState.Loading))
        {
            return;
        }

        _ = Task.Run(_keepAlive.Start);
        _sleep.Change(KeepAwake, Timeout.InfiniteTimeSpan);
    }

    private void LetSleep()
    {
        if (IsSpeaking || Volatile.Read(ref _pending) > 0 || Volatile.Read(ref _open) > 0)
        {
            _sleep.Change(KeepAwake, Timeout.InfiniteTimeSpan);
            return;
        }

        _keepAlive.Stop();
    }

    /// <summary>Begins a reply; feed it the text as it streams in, then complete it.</summary>
    /// <param name="onFirstAudio">Called once, when the reply's first audio is queued to play; on any thread.</param>
    /// <param name="silent">The reply is only written: nothing of it is spoken, as while muted, and nothing else is hushed.</param>
    public SpokenReply Begin(Action<DateTimeOffset>? onFirstAudio = null, bool silent = false)
    {
        Interlocked.Increment(ref _open);
        return new(this, Interlocked.Increment(ref _replies), _muted || silent, onFirstAudio);
    }

    /// <summary>
    /// Stops speaking at once; nothing of the replies begun so far is spoken after this. Any thread, and returns at once:
    /// the player is stopped on the thread pool, or by the loop before it plays anything more, whichever comes first.
    /// </summary>
    public void Hush()
    {
        CancellationTokenSource hushed;
        lock (_lock)
        {
            _hushedThrough = Interlocked.Read(ref _replies);
            hushed = _hush;
            _hush = new CancellationTokenSource();
        }

        Interlocked.Increment(ref _stopsAsked);
        _ = Task.Run(StopAsAskedAsync);
        hushed.Cancel(); // outside the lock: the cancelled request's callbacks run here. Not disposed: a sentence may still hold its token.
        SetSpeaking(false);
    }

    private bool IsHushed(long reply) => reply <= Interlocked.Read(ref _hushedThrough);

    private async Task StopAsAskedAsync()
    {
        await _playerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            CatchUpStops();
        }
        finally
        {
            _playerGate.Release();
        }
    }

    /// <summary>Gives the player the stops asked for since it last had one. Under <see cref="_playerGate"/>.</summary>
    private void CatchUpStops()
    {
        var asked = Interlocked.Read(ref _stopsAsked);
        if (_stopsDone < asked)
        {
            _stopsDone = asked;
            _player.Stop();
        }
    }

    private void Queue(Sentence sentence)
    {
        Interlocked.Increment(ref _pending);
        if (!_sentences.Writer.TryWrite(sentence))
        {
            Interlocked.Decrement(ref _pending); // disposed
        }
    }

    private async Task RunAsync()
    {
        var reader = _sentences.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            try
            {
                while (reader.TryRead(out var sentence))
                {
                    try
                    {
                        await SpeakAsync(sentence).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pending);
                    }
                }

                await PlayOutAsync(reader).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A handler of the events threw: the voice goes on with the next sentence.
                _logger.LogError(ex, "Raven's voice failed");
            }
        }
    }

    private async Task SpeakAsync(Sentence sentence)
    {
        var reply = sentence.Reply;
        CancellationToken hush;
        lock (_lock)
        {
            if (IsHushed(reply.Number) || reply.Dropped)
            {
                return;
            }

            hush = _hush.Token;
        }

        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(hush);
        using var watchdog = _time.CreateTimer(_ => Cancel(stalled), null, FirstAudioTimeout, Timeout.InfiniteTimeSpan);
        try
        {
            await foreach (var chunk in _tts.SpeakAsync(sentence.Text, stalled.Token).ConfigureAwait(false))
            {
                watchdog.Change(ChunkTimeout, Timeout.InfiniteTimeSpan);
                await _playerGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    // A hush that came while this chunk was generated stops the player first; one that comes while it
                    // is queued waits for the gate, and then stops it.
                    CatchUpStops();
                    if (_disposed || IsHushed(reply.Number))
                    {
                        return;
                    }

                    _player.Enqueue(chunk);
                }
                finally
                {
                    _playerGate.Release();
                }

                SetSpeaking(true, reply.Number);
                reply.HeardFirstAudio(_time.GetUtcNow());
            }
        }
        catch (OperationCanceledException) when (hush.IsCancellationRequested)
        {
            // Hushed.
        }
        catch (OperationCanceledException)
        {
            // Hung: likely the sidecar itself (a generation that never ends holds its lock), so every later sentence
            // would wait behind it. It is started again.
            _logger.LogWarning("Raven's voice stalled on \"{Sentence}\"", sentence.Text);
            _tts.Recover();
            Drop(reply, "Raven's voice hung and is starting again, so the rest of this answer is not spoken.");
        }
        catch (TextToSpeechNotReadyException ex)
        {
            Drop(reply, NotReady(ex.Status));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Raven could not speak \"{Sentence}\"", sentence.Text);
            Drop(reply, $"Raven could not speak: {ex.Message}");
        }
    }

    private static void Cancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The sentence ended meanwhile.
        }
    }

    /// <summary>The rest of the reply is not spoken; the user hears why once per reply, unless it is said elsewhere.</summary>
    private void Drop(SpokenReply reply, string? why)
    {
        if (reply.Drop() && why is not null)
        {
            Unspoken?.Invoke(this, why);
        }
    }

    /// <summary>Null when the status speaks for itself: a failure was reported as it happened, and with no engine picked Raven only writes.</summary>
    private static string? NotReady(TextToSpeechStatus status) => status.State switch
    {
        TextToSpeechState.Failed or TextToSpeechState.NoEngine => null,
        TextToSpeechState.Installing => "Raven's voice is being installed, so this answer is not spoken.",
        _ => "Raven's voice is still loading, so this answer is not spoken.",
    };

    /// <summary>
    /// Nothing more is queued: lets what plays play out, then closes the device, unless the next sentence comes first
    /// (then it plays on) or a hush came meanwhile. The player gets the length of its audio and a margin; past that it
    /// counts as hung, and is stopped.
    /// </summary>
    private async Task PlayOutAsync(ChannelReader<Sentence> reader)
    {
        CancellationToken hush;
        lock (_lock)
        {
            if (!_speaking)
            {
                return;
            }

            hush = _hush.Token;
        }

        var deadline = _time.GetUtcNow() + _player.Remaining + PlayOutMargin;
        try
        {
            while (_player.Remaining > TimeSpan.Zero && _time.GetUtcNow() < deadline)
            {
                if (reader.Count > 0)
                {
                    return;
                }

                await Task.Delay(PlayOutPoll, _time, hush).ConfigureAwait(false);
            }

            if (_player.Remaining > TimeSpan.Zero)
            {
                _logger.LogWarning("Speech did not play out in time; stopping it");
            }
            else
            {
                await Task.Delay(Tail, _time, hush).ConfigureAwait(false);
                if (reader.Count > 0 || _player.Remaining > TimeSpan.Zero)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return; // hushed: that stopped it already
        }

        await _playerGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            CatchUpStops();
            if (hush.IsCancellationRequested)
            {
                return; // hushed: that stopped it already, and said so
            }

            _player.Stop();
        }
        finally
        {
            _playerGate.Release();
        }

        SetSpeaking(false);
    }

    /// <summary>
    /// Flips <see cref="IsSpeaking"/> and tells it, in the order the flips happen. Speaking starts only for a reply not
    /// hushed by then: a hush between a chunk and this call leaves the voice quiet.
    /// </summary>
    /// <param name="reply">The reply whose audio starts it; for speaking only.</param>
    private void SetSpeaking(bool speaking, long reply = 0)
    {
        lock (_telling)
        {
            lock (_lock)
            {
                if (_speaking == speaking || (speaking && IsHushed(reply)))
                {
                    return;
                }

                _speaking = speaking;
            }

            if (!speaking)
            {
                _sleep.Change(KeepAwake, Timeout.InfiniteTimeSpan);
            }

            SpeakingChanged?.Invoke(this, speaking);
        }
    }

    /// <summary>Completes once every sentence queued so far has been spoken or dropped, and played out; for the tests.</summary>
    internal async Task WhenQuietAsync()
    {
        while (Volatile.Read(ref _pending) > 0 || IsSpeaking)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Nothing more is spoken: the player is stopped through the gate like every call to it, so a chunk being queued
    /// right now is stopped after it, and none is queued after this. A gate held past two seconds (a device that hangs
    /// opening) is not waited for longer; the hush's own stop follows it.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _sentences.Writer.TryComplete();
        Hush();
        // The loop ends once the hushed sentence lets go: after it, nothing touches the timer disposed below.
        _loop.Wait(TimeSpan.FromSeconds(2));
        _sleep.Dispose();
        _keepAlive.Stop();
        if (_playerGate.Wait(TimeSpan.FromSeconds(2)))
        {
            try
            {
                _player.Stop();
            }
            finally
            {
                _playerGate.Release();
            }
        }
    }

    private sealed record Sentence(SpokenReply Reply, string Text);

    /// <summary>One reply being spoken: fed its text as it streams in, in order, on one thread at a time, then completed.</summary>
    public sealed class SpokenReply
    {
        private readonly ReplyVoice _voice;
        private readonly bool _muted;
        private readonly Action<DateTimeOffset>? _onFirstAudio;
        private readonly SentenceChunker _chunker = new();
        private int _queued;
        private int _dropped;
        private int _heard;
        private int _completed;

        internal SpokenReply(ReplyVoice voice, long number, bool muted, Action<DateTimeOffset>? onFirstAudio)
        {
            _voice = voice;
            Number = number;
            _muted = muted;
            _onFirstAudio = onFirstAudio;
        }

        internal long Number { get; }

        internal bool Dropped => Volatile.Read(ref _dropped) == 1;

        /// <summary>Nothing more of it is spoken: muted, dropped, hushed, or its sentences are all queued.</summary>
        private bool Done => _muted || Dropped || _queued >= MaximumSentences || _voice.IsHushed(Number);

        /// <summary>The next piece of the reply's text; not even cut into sentences once nothing more of it is spoken.</summary>
        public void Add(string piece)
        {
            if (!Done)
            {
                Queue(_chunker.Add(piece));
            }
        }

        /// <summary>The reply is complete: what is left of it is a sentence too, and it no longer keeps the output awake.</summary>
        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 1)
            {
                return;
            }

            if (!Done)
            {
                Queue(_chunker.Flush());
            }

            Interlocked.Decrement(ref _voice._open);
        }

        private void Queue(IReadOnlyList<string> sentences)
        {
            foreach (var sentence in sentences)
            {
                if (_queued >= MaximumSentences)
                {
                    return;
                }

                var spoken = SpeechText.CleanForSpeech(sentence);
                if (spoken.Any(char.IsLetterOrDigit))
                {
                    _queued++;
                    _voice.Queue(new Sentence(this, SpeechText.EndSentence(spoken)));
                }
            }
        }

        /// <summary>True the first time: the reply is dropped now.</summary>
        internal bool Drop() => Interlocked.Exchange(ref _dropped, 1) == 0;

        internal void HeardFirstAudio(DateTimeOffset at)
        {
            if (Interlocked.Exchange(ref _heard, 1) == 0)
            {
                _onFirstAudio?.Invoke(at);
            }
        }
    }
}
