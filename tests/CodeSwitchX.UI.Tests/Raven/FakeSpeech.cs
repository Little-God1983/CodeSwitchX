using System.Runtime.CompilerServices;
using CodeSwitchX.Voice.Speech;
using LittleGod.Speech;
using LittleGod.Speech.Speaking;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>A voice that says every sentence at once, as one chunk, and remembers it.</summary>
internal sealed class FakeSpeech : ITextToSpeech
{
    private readonly List<string> _spoken = [];

    public List<string> Spoken
    {
        get
        {
            lock (_spoken)
            {
                return [.. _spoken];
            }
        }
    }

    public int Prepares { get; private set; }

    /// <summary>While set and not completed, every sentence waits for it before its audio.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>While set, every sentence fails with it, as a sidecar that broke.</summary>
    public Exception? Fails { get; set; }

    public TextToSpeechStatus Status { get; private set; } = new(TextToSpeechState.Ready);

    public bool SpeaksSentencesTogether => true;

    public event EventHandler<TextToSpeechStatus>? StatusChanged;

    public void Report(TextToSpeechStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    public void Prepare(bool install) => Prepares++;

    public void Recover()
    {
    }

    public async IAsyncEnumerable<SpeechChunk> SpeakAsync(string text, SpeechRequest? request, [EnumeratorCancellation] CancellationToken ct)
    {
        lock (_spoken)
        {
            _spoken.Add(text);
        }

        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        if (Fails is { } error)
        {
            throw error;
        }

        yield return new SpeechChunk(new byte[480], 24000);
    }

    /// <summary>
    /// A reply voice over this fake, a player that plays at once and no keep-alive; on real time, so it plays out. It says
    /// at most the sentences the app's voice says (#328), as the panel counts against them.
    /// </summary>
    public ReplyVoice NewVoice(ISpeechPlayer? player = null) =>
        new(this, player ?? new InstantPlayer(), new NoKeepAlive(), TimeProvider.System, NullLogger<ReplyVoice>.Instance,
            new SpeakingOptions { MaximumSentences = SpeechSettings.MaximumSentences });

    /// <summary>Plays what it is given the moment it is given: all of it is heard at once.</summary>
    internal sealed class InstantPlayer : ISpeechPlayer
    {
        private int _stops;
        private long _queued;

        public int Stops => Volatile.Read(ref _stops);

        public TimeSpan Remaining => TimeSpan.Zero;

        public bool IsOpening => false;

        public TimeSpan Latency => TimeSpan.Zero;

        public TimeSpan Queued => TimeSpan.FromTicks(Volatile.Read(ref _queued));

        public TimeSpan Position => Queued;

        public event EventHandler<float>? LevelChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<DroppedAudio>? Dropped
        {
            add { }
            remove { }
        }

        public void Enqueue(SpeechChunk chunk, Func<bool>? hushed = null) => Interlocked.Add(ref _queued, chunk.Duration.Ticks);

        public void Stop()
        {
            Interlocked.Increment(ref _stops);
            Interlocked.Exchange(ref _queued, 0);
        }

        public void Dispose()
        {
        }
    }

    private sealed class NoKeepAlive : IAudioKeepAlive
    {
        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }
}
