namespace CodeSwitchX.Voice.Tests.Speech;

using System.Runtime.CompilerServices;
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Speech;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class ReplyVoiceTests : IDisposable
{
    private readonly FakeSpeech _tts = new();
    private readonly FakePlayer _player = new();
    private readonly FakeKeepAlive _keepAlive = new();
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly ReplyVoice _voice;

    public ReplyVoiceTests()
    {
        _voice = new ReplyVoice(_tts, _player, _keepAlive, _time, NullLogger<ReplyVoice>.Instance);
    }

    public void Dispose() => _voice.Dispose();

    [Fact]
    public async Task A_sentence_is_spoken_while_the_rest_of_the_reply_streams_in()
    {
        var reply = _voice.Begin();
        reply.Add("Two chats need you. The API");
        await Until(() => _tts.Spoken.Count == 1);
        _tts.Spoken.ShouldBe(["Two chats need you."]);

        reply.Add(" one waits.");
        reply.Complete();
        await _voice.WhenQuietAsync();
        _tts.Spoken.ShouldBe(["Two chats need you.", "The API one waits."]);
        _player.Played.ShouldBe(2);
    }

    [Fact]
    public async Task At_most_three_sentences_of_a_reply_are_spoken()
    {
        var reply = _voice.Begin();
        reply.Add("One. Two. Three. Four. Five.");
        reply.Complete();
        await _voice.WhenQuietAsync();
        _tts.Spoken.ShouldBe(["One.", "Two.", "Three."]);
    }

    [Fact]
    public async Task What_is_spoken_is_cleaned_and_a_sentence_without_words_is_left_out()
    {
        var reply = _voice.Begin();
        reply.Add("**Done** ✅\n---\nSee `auth.cs`.");
        reply.Complete();
        await _voice.WhenQuietAsync();
        _tts.Spoken.ShouldBe(["Done.", "See auth.cs."]);
    }

    [Fact]
    public async Task Speaking_starts_with_the_first_audio_and_ends_once_it_has_played_out()
    {
        var changes = new List<bool>();
        _voice.SpeakingChanged += (_, speaking) => { lock (changes) { changes.Add(speaking); } };
        _player.Remaining = TimeSpan.FromMilliseconds(200);
        var reply = _voice.Begin();
        reply.Add("Hello there.");
        reply.Complete();

        await Until(() => _voice.IsSpeaking);
        _player.Stops.ShouldBe(0);
        _player.Remaining = TimeSpan.Zero;
        await _voice.WhenQuietAsync();

        lock (changes)
        {
            changes.ShouldBe([true, false]);
        }

        _player.Stops.ShouldBe(1); // the device is closed once it is done
    }

    [Fact]
    public async Task A_hush_stops_at_once_and_the_rest_of_the_reply_is_never_spoken()
    {
        _tts.Gate = new TaskCompletionSource();
        var reply = _voice.Begin();
        reply.Add("First sentence. Second sentence. ");
        await Until(() => _tts.Spoken.Count == 1);

        _voice.Hush();
        await Until(() => _player.Stops == 1);
        _tts.Cancelled.ShouldBeTrue();
        _tts.Gate.TrySetResult();
        reply.Add("Third sentence.");
        reply.Complete();
        await _voice.WhenQuietAsync();

        _tts.Spoken.ShouldBe(["First sentence."]);
        _player.Played.ShouldBe(0);
        _voice.IsSpeaking.ShouldBeFalse();
    }

    [Fact]
    public async Task A_reply_begun_after_a_hush_is_spoken()
    {
        _voice.Begin().Add("Old. ");
        _voice.Hush();
        var next = _voice.Begin();
        next.Add("New one.");
        next.Complete();
        await _voice.WhenQuietAsync();
        _tts.Spoken.ShouldNotContain("Old.");
        _tts.Spoken.ShouldContain("New one.");
    }

    [Fact]
    public async Task Nothing_is_spoken_while_muted_and_muting_hushes()
    {
        _voice.Muted = true;
        await Until(() => _player.Stops == 1);
        var reply = _voice.Begin();
        reply.Add("Not said.");
        reply.Complete();
        _voice.Muted = false;
        var after = _voice.Begin();
        after.Add("Said.");
        after.Complete();
        await _voice.WhenQuietAsync();
        _tts.Spoken.ShouldBe(["Said."]);
    }

    [Fact]
    public async Task A_voice_that_is_not_ready_drops_the_reply_with_one_note()
    {
        var notes = new List<string>();
        _voice.Unspoken += (_, why) => { lock (notes) { notes.Add(why); } };
        _tts.NotReady = new TextToSpeechStatus(TextToSpeechState.Loading);
        var reply = _voice.Begin();
        reply.Add("One. Two. Three.");
        reply.Complete();
        await _voice.WhenQuietAsync();

        _tts.Spoken.ShouldBe(["One."]);
        notes.ShouldBe(["Raven's voice is still loading, so this answer is not spoken."]);
    }

    [Fact]
    public async Task A_failed_voice_was_reported_already_and_is_not_noted_again()
    {
        var notes = new List<string>();
        _voice.Unspoken += (_, why) => { lock (notes) { notes.Add(why); } };
        _tts.NotReady = new TextToSpeechStatus(TextToSpeechState.Failed, "no GPU");
        var reply = _voice.Begin();
        reply.Add("One.");
        reply.Complete();
        await _voice.WhenQuietAsync();
        notes.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_first_audio_is_told_once()
    {
        var heard = 0;
        var reply = _voice.Begin(_ => Interlocked.Increment(ref heard));
        reply.Add("One. Two.");
        reply.Complete();
        await _voice.WhenQuietAsync();
        heard.ShouldBe(1);
    }

    [Fact]
    public async Task A_coming_reply_wakes_the_output_and_muted_it_stays_asleep()
    {
        _voice.Expect();
        await Until(() => _keepAlive.On);

        _keepAlive.On = false;
        _voice.Muted = true;
        _voice.Expect();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        _keepAlive.On.ShouldBeFalse();
    }

    [Fact]
    public async Task Waking_the_output_never_waits_for_the_device()
    {
        _keepAlive.Opening = new TaskCompletionSource();

        var expect = Task.Run(_voice.Expect, TestContext.Current.CancellationToken);

        (await Task.WhenAny(expect, Task.Delay(1000, TestContext.Current.CancellationToken))).ShouldBe(expect, "the mic press must not wait for a headset to wake");
        _keepAlive.Opening.TrySetResult();
        await Until(() => _keepAlive.On);
    }

    [Fact]
    public async Task The_output_sleeps_again_a_while_after_the_voice_went_quiet()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var keepAlive = new FakeKeepAlive();
        using var voice = new ReplyVoice(_tts, _player, keepAlive, time, NullLogger<ReplyVoice>.Instance);
        voice.Expect();
        await Until(() => keepAlive.On);
        time.Advance(ReplyVoice.KeepAwake - TimeSpan.FromSeconds(1));
        keepAlive.On.ShouldBeTrue();
        time.Advance(TimeSpan.FromSeconds(1));
        keepAlive.On.ShouldBeFalse();
    }

    [Fact]
    public async Task A_reply_the_brain_still_works_on_keeps_the_output_awake()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var keepAlive = new FakeKeepAlive();
        using var voice = new ReplyVoice(_tts, _player, keepAlive, time, NullLogger<ReplyVoice>.Instance);
        voice.Expect();
        await Until(() => keepAlive.On);
        var reply = voice.Begin(); // the brain thinks, with tools, for minutes

        time.Advance(ReplyVoice.KeepAwake * 3);
        keepAlive.On.ShouldBeTrue("the first word of the answer must not be lost to a sleeping headset");

        reply.Complete();
        time.Advance(ReplyVoice.KeepAwake);
        keepAlive.On.ShouldBeFalse();
    }

    [Fact]
    public async Task A_hush_never_waits_for_the_device_and_what_was_opening_is_stopped_after()
    {
        _player.Opening = new TaskCompletionSource();
        var reply = _voice.Begin();
        reply.Add("A sentence that opens the device. ");
        await Until(() => _player.Enqueuing);

        var hush = Task.Run(_voice.Hush, TestContext.Current.CancellationToken);

        (await Task.WhenAny(hush, Task.Delay(1000, TestContext.Current.CancellationToken))).ShouldBe(hush, "a mic press must not wait for a Bluetooth headset to open");
        _player.Opening.TrySetResult();
        await Until(() => _player.Stops == 1);
        _player.Log.ShouldBe(["enqueue", "stop"]); // the chunk that was being queued does not play on
        _voice.IsSpeaking.ShouldBeFalse();
    }

    [Fact]
    public async Task Speaking_is_told_in_the_order_it_changes()
    {
        var told = new List<bool>();
        _voice.SpeakingChanged += (_, speaking) =>
        {
            if (speaking)
            {
                Thread.Sleep(200); // the loop thread is pre-empted between the flip and its telling
            }

            lock (told)
            {
                told.Add(speaking);
            }
        };
        _player.Remaining = TimeSpan.FromSeconds(10);
        var reply = _voice.Begin();
        reply.Add("Hello there. ");
        await Until(() => _player.Played == 1);

        _voice.Hush();
        await Until(() => { lock (told) { return told.Count == 2; } });

        lock (told)
        {
            told.ShouldBe([true, false], "the panel must end on not speaking");
        }
    }

    [Fact]
    public async Task A_reply_hushed_before_its_first_audio_is_told_does_not_start_speaking()
    {
        var told = new List<bool>();
        _voice.SpeakingChanged += (_, speaking) => { lock (told) { told.Add(speaking); } };
        _tts.Gate = new TaskCompletionSource();
        var reply = _voice.Begin();
        reply.Add("Hello there. ");
        await Until(() => _tts.Spoken.Count == 1);

        _voice.Hush();
        _tts.Gate.TrySetResult();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        _voice.IsSpeaking.ShouldBeFalse();
        lock (told)
        {
            told.ShouldNotContain(true);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the condition never came true");
            await Task.Delay(5);
        }
    }

    private sealed class FakeSpeech : ITextToSpeech
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

        /// <summary>When set, a sentence's audio waits for it.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public TextToSpeechStatus? NotReady { get; set; }

        public bool Cancelled { get; private set; }

        public TextToSpeechStatus Status => TextToSpeechStatus.Off;

        public event EventHandler<TextToSpeechStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public void Prepare(bool install)
        {
        }

        public async IAsyncEnumerable<SpeechChunk> SpeakAsync(string text, [EnumeratorCancellation] CancellationToken ct)
        {
            lock (_spoken)
            {
                _spoken.Add(text);
            }

            if (NotReady is { } status)
            {
                throw new TextToSpeechNotReadyException(status);
            }

            if (Gate is { } gate)
            {
                using var _ = ct.Register(() => Cancelled = true);
                await gate.Task;
            }

            ct.ThrowIfCancellationRequested();
            yield return new SpeechChunk(new byte[480], 24000);
        }
    }

    private sealed class FakeKeepAlive : IAudioKeepAlive
    {
        public volatile bool On;

        /// <summary>When set, opening the device waits for it.</summary>
        public TaskCompletionSource? Opening { get; set; }

        public void Start()
        {
            Opening?.Task.Wait();
            On = true;
        }

        public void Stop() => On = false;

        public void Dispose()
        {
        }
    }

    private sealed class FakePlayer : ISpeechPlayer
    {
        private int _played;
        private int _stops;

        public int Played => Volatile.Read(ref _played);

        public int Stops => Volatile.Read(ref _stops);

        public TimeSpan Remaining { get; set; }

        public event EventHandler<float>? LevelChanged
        {
            add { }
            remove { }
        }

        /// <summary>When set, the first chunk opens the device, which waits for it.</summary>
        public TaskCompletionSource? Opening { get; set; }

        public volatile bool Enqueuing;

        public List<string> Log { get; } = [];

        public void Enqueue(SpeechChunk chunk)
        {
            Enqueuing = true;
            Opening?.Task.Wait();
            lock (Log)
            {
                Log.Add("enqueue");
            }

            Interlocked.Increment(ref _played);
        }

        public void Stop()
        {
            lock (Log)
            {
                Log.Add("stop");
            }

            Interlocked.Increment(ref _stops);
        }

        public void Dispose()
        {
        }
    }
}
