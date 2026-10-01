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
    private readonly Task _loop;

    /// <summary>How many replies have begun: each one's number.</summary>
    private long _replies;

    /// <summary>Replies numbered up to this one are not spoken (any more): they began before the last hush.</summary>
    private long _hushedThrough;
    private CancellationTokenSource _hush = new();
    private volatile bool _muted;

    /// <summary>Sentences queued and not yet spoken or dropped.</summary>
    private int _pending;
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
    /// first word to waking up. It sleeps again <see cref="KeepAwake"/> after the voice last went quiet. Any thread.
    /// </summary>
    public void Expect()
    {
        if (_muted)
        {
            return;
        }

        _keepAlive.Start();
        _sleep.Change(KeepAwake, Timeout.InfiniteTimeSpan);
    }

    private void LetSleep()
    {
        if (IsSpeaking || Volatile.Read(ref _pending) > 0)
        {
            _sleep.Change(KeepAwake, Timeout.InfiniteTimeSpan);
            return;
        }

        _keepAlive.Stop();
    }

    /// <summary>Begins a reply; feed it the text as it streams in, then complete it.</summary>
    /// <param name="onFirstAudio">Called once, when the reply's first audio is queued to play; on any thread.</param>
    public SpokenReply Begin(Action<DateTimeOffset>? onFirstAudio = null) =>
        new(this, Interlocked.Increment(ref _replies), _muted, onFirstAudio);

    /// <summary>Stops speaking at once; nothing of the replies begun so far is spoken after this. Any thread.</summary>
    public void Hush()
    {
        CancellationTokenSource hushed;
        lock (_lock)
        {
            _hushedThrough = Interlocked.Read(ref _replies);
            hushed = _hush;
            _hush = new CancellationTokenSource();
            _player.Stop();
        }

        hushed.Cancel(); // outside the lock: the cancelled request's callbacks run here. Not disposed: a sentence may still hold its token.
        SetSpeaking(false);
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
            if (reply.Number <= _hushedThrough || reply.Dropped)
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
                lock (_lock)
                {
                    if (reply.Number <= _hushedThrough)
                    {
                        return;
                    }

                    _player.Enqueue(chunk);
                }

                SetSpeaking(true);
                reply.HeardFirstAudio(_time.GetUtcNow());
            }
        }
        catch (OperationCanceledException) when (hush.IsCancellationRequested)
        {
            // Hushed.
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Raven's voice stalled on \"{Sentence}\"", sentence.Text);
            Drop(reply, "Raven's voice took too long, so the rest of this answer is not spoken.");
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

    /// <summary>Null when the status speaks for itself: a failure was reported as it happened.</summary>
    private static string? NotReady(TextToSpeechStatus status) => status.State switch
    {
        TextToSpeechState.Failed => null,
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

        lock (_lock)
        {
            if (hush.IsCancellationRequested)
            {
                return;
            }

            _player.Stop();
        }

        SetSpeaking(false);
    }

    private void SetSpeaking(bool speaking)
    {
        lock (_lock)
        {
            if (_speaking == speaking)
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

    /// <summary>Completes once every sentence queued so far has been spoken or dropped, and played out; for the tests.</summary>
    internal async Task WhenQuietAsync()
    {
        while (Volatile.Read(ref _pending) > 0 || IsSpeaking)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _sentences.Writer.TryComplete();
        Hush();
        _sleep.Dispose();
        _keepAlive.Stop();
    }

    private sealed record Sentence(SpokenReply Reply, string Text);

    /// <summary>One reply being spoken: fed its text as it streams in, in order, on one thread at a time.</summary>
    public sealed class SpokenReply
    {
        private readonly ReplyVoice _voice;
        private readonly bool _muted;
        private readonly Action<DateTimeOffset>? _onFirstAudio;
        private readonly SentenceChunker _chunker = new();
        private int _queued;
        private int _dropped;
        private int _heard;

        internal SpokenReply(ReplyVoice voice, long number, bool muted, Action<DateTimeOffset>? onFirstAudio)
        {
            _voice = voice;
            Number = number;
            _muted = muted;
            _onFirstAudio = onFirstAudio;
        }

        internal long Number { get; }

        internal bool Dropped => Volatile.Read(ref _dropped) == 1;

        /// <summary>The next piece of the reply's text.</summary>
        public void Add(string piece)
        {
            if (!_muted && !Dropped)
            {
                Queue(_chunker.Add(piece));
            }
        }

        /// <summary>The reply is complete: what is left of it is a sentence too.</summary>
        public void Complete()
        {
            if (!_muted && !Dropped)
            {
                Queue(_chunker.Flush());
            }
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
