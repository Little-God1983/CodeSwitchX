using CodeSwitchX.Voice.Listening;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.Voice.Tests.Listening;

public sealed class TurnDetectorTests
{
    private const int Frame = SileroVad.FrameSamples; // 32 ms
    private readonly ScriptedVad _vad = new();
    private readonly ScriptedTurnEnd _turn = new();
    private readonly TurnDetector _detector;

    public TurnDetectorTests() => _detector = new TurnDetector(_vad, _turn, NullLogger.Instance);

    [Fact]
    public void A_burst_shorter_than_a_quarter_second_is_no_turn()
    {
        var events = Feed(Silence(1), Speech(0.2), Silence(1.5));

        events.ShouldBeEmpty();
        _turn.Calls.ShouldBe(0);
    }

    // #217: "Raven." and "Yes." are shorter than the half second a turn needs
    [Fact]
    public void A_word_on_its_own_ends_as_a_short_turn_once_a_second_of_silence_follows()
    {
        var events = Feed(Silence(1), Speech(0.4), Silence(0.9));
        events.ShouldBeEmpty();

        events = Feed(Silence(0.2));

        var ended = events.ShouldHaveSingleItem().ShouldBeOfType<TurnEvent.Ended>();
        ended.Short.ShouldBeTrue();
        Seconds(ended.Clip).ShouldBeInRange(1.0, 1.2); // 0.5 s pre-roll + 0.4 s + 0.2 s of the pause
        _turn.Calls.ShouldBe(0, "no turn to end: nothing to ask Smart Turn");
    }

    // #219: the rest of a request is timed from when the speech ended, which is not always the 0.2 s pause before the end
    [Fact]
    public void A_turn_says_how_long_its_speech_had_been_over()
    {
        _turn.Answers.Enqueue(0.9);
        Feed(Speech(1.0), Silence(0.5)).OfType<TurnEvent.Ended>().ShouldHaveSingleItem().Silence.TotalSeconds.ShouldBe(0.2, 0.04);

        _turn.Answers.Enqueue(0.1); // "check chat three and …": Smart Turn waits, and the turn ends after 3 s of silence
        Feed(Speech(1.0), Silence(3.2)).OfType<TurnEvent.Ended>().ShouldHaveSingleItem().Silence.TotalSeconds.ShouldBe(3.0, 0.04);
    }

    // #217: "Raven," and the comma's pause were dropped as a cough, and the words after it came without the name
    [Fact]
    public void A_short_word_and_the_words_after_a_pause_are_one_turn()
    {
        _turn.Answers.Enqueue(0.9);
        var name = 0.25f;

        var events = Feed(Silence(1), Audio(name, Frame * 15, speech: true), Silence(0.35), Speech(1.1), Silence(0.5));

        events.OfType<TurnEvent.Started>().ShouldHaveSingleItem();
        var ended = events.OfType<TurnEvent.Ended>().ShouldHaveSingleItem();
        ended.Short.ShouldBeFalse();
        ended.Clip.ShouldContain(name);
    }

    [Fact]
    public void Half_a_second_of_speech_starts_the_turn_and_a_complete_pause_ends_it_after_0_2_s()
    {
        _turn.Answers.Enqueue(0.9);

        var events = Feed(Silence(1), Speech(1.0), Silence(1));

        events.Count.ShouldBe(2);
        events[0].ShouldBeOfType<TurnEvent.Started>();
        var clip = events[1].ShouldBeOfType<TurnEvent.Ended>().Clip;
        // 0.5 s pre-roll + 1 s speech + 0.2 s of the pause, give or take a frame each
        Seconds(clip).ShouldBeInRange(1.6, 1.8);
        _turn.Calls.ShouldBe(1);
    }

    [Fact]
    public void A_pause_Smart_Turn_calls_incomplete_does_not_end_the_turn_and_more_speech_continues_it()
    {
        _turn.Answers.Enqueue(0.1); // "I'd like to open the …"
        _turn.Answers.Enqueue(0.9);

        var events = Feed(Speech(1.0), Silence(1.0), Speech(1.0), Silence(0.5));

        events.OfType<TurnEvent.Started>().Count().ShouldBe(1);
        Seconds(events.OfType<TurnEvent.Ended>().ShouldHaveSingleItem().Clip).ShouldBeGreaterThan(3.0);
        _turn.Calls.ShouldBe(2);
    }

    [Fact]
    public void Three_seconds_of_silence_end_the_turn_whatever_Smart_Turn_says()
    {
        _turn.Answers.Enqueue(0.1);

        var events = Feed(Speech(1.0), Silence(2.9));
        events.OfType<TurnEvent.Ended>().ShouldBeEmpty();

        events = Feed(Silence(0.2));
        events.OfType<TurnEvent.Ended>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Smart_Turn_failing_falls_back_to_the_three_seconds()
    {
        _turn.Throws = true;

        var events = Feed(Speech(1.0), Silence(3.2));

        events.OfType<TurnEvent.Ended>().ShouldHaveSingleItem();
    }

    [Fact]
    public void The_pre_roll_carries_the_first_syllable_the_VAD_was_late_for()
    {
        _turn.Answers.Enqueue(0.9);
        var marker = 0.25f;

        var events = Feed(Silence(1), Audio(marker, Frame, speech: false), Speech(1.0), Silence(0.5));

        events.OfType<TurnEvent.Ended>().Single().Clip.ShouldContain(marker);
    }

    [Fact]
    public void A_turn_ends_at_two_minutes()
    {
        var events = Feed(Speech(121));

        Seconds(events.OfType<TurnEvent.Ended>().ShouldHaveSingleItem().Clip).ShouldBeLessThanOrEqualTo(120.6);
    }

    [Fact]
    public void Reset_drops_a_half_spoken_turn()
    {
        Feed(Speech(1.0));

        _detector.Reset();
        var events = Feed(Silence(4));

        events.ShouldBeEmpty();
        _vad.Resets.ShouldBe(1);
    }

    [Fact]
    public void While_speech_is_ignored_it_starts_no_turn()
    {
        _detector.IgnoreSpeech = true;

        Feed(Speech(2.0), Silence(1)).ShouldBeEmpty();

        _detector.IgnoreSpeech = false;
        _turn.Answers.Enqueue(0.9);
        Feed(Speech(1.0), Silence(0.5)).OfType<TurnEvent.Ended>().ShouldHaveSingleItem();
    }

    [Fact]
    public void A_dip_under_the_start_level_but_over_the_continue_level_is_still_speech()
    {
        _turn.Answers.Enqueue(0.9);

        var events = Feed(Speech(0.3), Audio(0.1f, Frame * 5, speech: true, probability: 0.4f), Speech(0.3), Silence(0.5));

        events.OfType<TurnEvent.Started>().ShouldHaveSingleItem();
        _turn.Calls.ShouldBe(1, "the dip was no pause");
    }

    [Fact]
    public void A_dropped_burst_does_not_wipe_the_pre_roll_of_the_speech_right_after_it()
    {
        _turn.Answers.Enqueue(0.9);
        var marker = 0.25f;

        var events = Feed(Silence(1), Speech(0.3), Audio(marker, Frame, speech: false), Silence(0.25), Speech(1.0), Silence(0.5));

        events.OfType<TurnEvent.Ended>().Single().Clip.ShouldContain(marker);
    }

    [Fact]
    public void Ignoring_speech_drops_a_turn_that_has_not_started_yet_at_once()
    {
        Feed(Speech(0.3));
        _detector.IgnoreSpeech = true;

        Feed(Speech(1.0), Silence(0.5)).ShouldBeEmpty();

        _turn.Calls.ShouldBe(0);
    }

    [Fact]
    public void A_started_turn_goes_on_through_ignored_speech_and_ends_like_any_turn()
    {
        Feed(Speech(1.0)).OfType<TurnEvent.Started>().ShouldHaveSingleItem();
        _detector.IgnoreSpeech = true; // an earlier answer starts to play while the user is still talking
        _turn.Answers.Enqueue(0.9);

        Feed(Speech(1.0)).ShouldBeEmpty();
        var events = Feed(Silence(0.5));

        var clip = events.OfType<TurnEvent.Ended>().ShouldHaveSingleItem().Clip;
        Seconds(clip).ShouldBeInRange(2.1, 2.3); // 2 s of speech + the 0.2 s pause
        _turn.Calls.ShouldBe(1, "Smart Turn decides, as with speech heard");
    }

    [Fact]
    public void While_speech_is_ignored_the_words_after_a_short_pause_stay_in_the_started_turn()
    {
        Feed(Speech(1.0)).OfType<TurnEvent.Started>().ShouldHaveSingleItem();
        _detector.IgnoreSpeech = true;
        _turn.Answers.Enqueue(0.1); // "Open the chat about …"
        _turn.Answers.Enqueue(0.9);
        var marker = 0.25f; // "… the login bug"

        var events = Feed(Silence(0.25), Audio(marker, Frame * 10, speech: true), Silence(0.5));

        events.OfType<TurnEvent.Ended>().ShouldHaveSingleItem().Clip.ShouldContain(marker);
        _turn.Calls.ShouldBe(2);
    }

    [Fact]
    public void While_speech_is_ignored_a_started_turn_still_gives_up_after_three_seconds()
    {
        Feed(Speech(1.0));
        _detector.IgnoreSpeech = true;
        _turn.Answers.Enqueue(0.1);

        Feed(Silence(2.9)).ShouldBeEmpty();

        Feed(Silence(0.2)).OfType<TurnEvent.Ended>().ShouldHaveSingleItem();
    }

    [Fact]
    public void While_speech_is_ignored_a_started_turn_keeps_the_continue_level()
    {
        Feed(Speech(1.0));
        _detector.IgnoreSpeech = true;

        Feed(Audio(0.1f, Samples(0.5), speech: true, probability: 0.4f)).ShouldBeEmpty();

        _turn.Calls.ShouldBe(0, "a frame between the two levels is still speech");
    }

    [Fact]
    public void With_speech_heard_again_a_started_turn_still_asks_Smart_Turn_at_its_pause()
    {
        Feed(Speech(1.0));
        _detector.IgnoreSpeech = true;
        Feed(Speech(0.5));
        _detector.IgnoreSpeech = false;
        _turn.Answers.Enqueue(0.1);

        Feed(Silence(0.5)).ShouldBeEmpty();

        _turn.Calls.ShouldBe(1);
    }

    [Fact]
    public void Raven_s_own_voice_is_not_kept_as_pre_roll_while_speech_is_ignored()
    {
        _turn.Answers.Enqueue(0.9);
        var marker = 0.25f;
        _detector.IgnoreSpeech = true;
        Feed(Audio(marker, Frame * 3, speech: false));
        _detector.IgnoreSpeech = false;

        var events = Feed(Speech(1.0), Silence(0.5));

        events.OfType<TurnEvent.Ended>().Single().Clip.ShouldNotContain(marker);
    }

    private static double Seconds(float[] clip) => clip.Length / 16_000.0;

    private List<TurnEvent> Feed(params float[][] parts)
    {
        var events = new List<TurnEvent>();
        foreach (var part in parts)
        {
            for (var i = 0; i + Frame <= part.Length; i += Frame)
            {
                if (_detector.Step(part.AsSpan(i, Frame)) is { } e)
                {
                    events.Add(e);
                }
            }
        }

        return events;
    }

    private float[] Speech(double seconds) => Audio(0.5f, Samples(seconds), speech: true);

    private float[] Silence(double seconds) => Audio(0f, Samples(seconds), speech: false);

    private static int Samples(double seconds) => (int)Math.Round(seconds * 16_000 / Frame) * Frame;

    /// <summary>Audio whose frames the scripted VAD reads back: the first sample of each frame says how likely speech is.</summary>
    private float[] Audio(float value, int samples, bool speech, float? probability = null)
    {
        var audio = new float[samples];
        Array.Fill(audio, value);
        for (var i = 0; i < samples; i += Frame)
        {
            _vad.Script.Enqueue(probability ?? (speech ? 0.9f : 0.05f));
        }

        return audio;
    }

    private sealed class ScriptedVad : IVoiceActivity
    {
        public Queue<float> Script { get; } = new();

        public int Resets { get; private set; }

        public float Step(ReadOnlySpan<float> frame) => Script.Dequeue();

        public void Reset() => Resets++;

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedTurnEnd : ITurnEnd
    {
        public Queue<double> Answers { get; } = new();

        public int Calls { get; private set; }

        public bool Throws { get; set; }

        public double Complete(ReadOnlySpan<float> turn16k)
        {
            Calls++;
            turn16k.Length.ShouldBeLessThanOrEqualTo(8 * 16_000 + Frame);
            return Throws ? throw new InvalidOperationException("model died") : Answers.Count > 0 ? Answers.Dequeue() : 0.9;
        }

        public void Dispose()
        {
        }
    }
}
