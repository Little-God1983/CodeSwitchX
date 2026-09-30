namespace CodeSwitchX.Voice.Tests.Audio;

using CodeSwitchX.Voice.Audio;

public sealed class SpeechGateTests
{
    private static readonly TimeSpan Block = TimeSpan.FromMilliseconds(10); // what WASAPI hands over on the RØDE input
    private const float RoomNoise = 0.0004f;  // the loudest block of three seconds of a quiet room on the RØDE input
    private const float Speech = 0.05f;
    private const float QuietSpeech = 0.007f; // normal speech on the RØDE Connect Virtual Input (2026-09-30)
    private const float WordGap = 0.0012f;    // breath and the fade of the last syllable between two words

    private static SpeechGate Feed(params (float Rms, int Blocks)[] parts)
    {
        var gate = new SpeechGate();
        foreach (var (rms, blocks) in parts)
        {
            for (var i = 0; i < blocks; i++)
            {
                gate.Step(rms, Block);
            }
        }

        return gate;
    }

    private static (float Rms, int Blocks)[] Repeat(int times, params (float Rms, int Blocks)[] parts) =>
        Enumerable.Repeat(parts, times).SelectMany(p => p).ToArray();

    [Fact]
    public void The_defaults_open_between_0_003_and_0_01_at_five_times_the_floor_and_need_half_a_second_in_50_ms_runs()
    {
        var config = new SpeechGate.Config();

        config.MinimumOpenRms.ShouldBe(0.003f);
        config.MaximumOpenRms.ShouldBe(0.01f);
        config.AboveFloor.ShouldBe(5f);
        config.MinimumRun.ShouldBe(TimeSpan.FromMilliseconds(50));
        config.MinimumSpeech.ShouldBe(TimeSpan.FromSeconds(0.5));
    }

    [Fact]
    public void A_fresh_gate_has_heard_nothing()
    {
        new SpeechGate().Read().HeardSpeech.ShouldBeFalse();
    }

    [Fact]
    public void Room_noise_alone_is_no_speech_however_long_it_lasts()
    {
        Feed((RoomNoise, 12_000)).Read().HeardSpeech.ShouldBeFalse();
    }

    [Fact]
    public void Half_a_second_of_speech_is_speech()
    {
        Feed((RoomNoise, 50), (Speech, 50)).Read().HeardSpeech.ShouldBeTrue();
    }

    [Fact]
    public void Just_under_half_a_second_of_speech_is_not()
    {
        Feed((RoomNoise, 50), (Speech, 49)).Read().HeardSpeech.ShouldBeFalse();
    }

    [Fact]
    public void Speech_broken_up_by_pauses_adds_up()
    {
        Feed((Speech, 20), (RoomNoise, 30), (Speech, 20), (RoomNoise, 30), (Speech, 10)).Read().HeardSpeech.ShouldBeTrue();
    }

    // The bug: a fixed open level of 0.01 threw away normal speech on an input that delivers it at 0.007.
    [Fact]
    public void Quiet_speech_over_a_quiet_room_is_speech()
    {
        var reading = Feed((RoomNoise, 50), (QuietSpeech, 20), (RoomNoise, 10), (QuietSpeech, 30), (RoomNoise, 50)).Read();

        reading.OpenRms.ShouldBe(0.003f, "five times the room is under the minimum");
        reading.HeardSpeech.ShouldBeTrue();
    }

    // Talking from the press to the release leaves no lead-in: the floor is then read from the gaps between words.
    [Fact]
    public void Quiet_speech_from_the_first_block_to_the_last_is_speech()
    {
        var reading = Feed(Repeat(10, (QuietSpeech, 8), (WordGap, 2))).Read();

        reading.OpenRms.ShouldBe(WordGap * 5, 1e-6f);
        reading.HeardSpeech.ShouldBeTrue();
    }

    // A Bluetooth headset hands over exact zeros while it wakes up, then its own hiss.
    [Fact]
    public void Digital_silence_is_left_out_of_the_room_noise()
    {
        const float Hiss = 0.003f;

        var reading = Feed((0f, 300), (Hiss, 100)).Read();

        reading.OpenRms.ShouldBe(0.01f, "five times the hiss, capped at the maximum");
        reading.HeardSpeech.ShouldBeFalse();
    }

    [Fact]
    public void Speech_after_digital_silence_is_speech()
    {
        Feed((0f, 300), (RoomNoise, 50), (QuietSpeech, 60)).Read().HeardSpeech.ShouldBeTrue();
    }

    [Fact]
    public void A_clip_of_nothing_but_digital_silence_is_no_speech()
    {
        var reading = Feed((0f, 500)).Read();

        reading.HeardSpeech.ShouldBeFalse();
        reading.OpenRms.ShouldBe(0.003f);
    }

    [Fact]
    public void The_open_level_never_drops_under_the_minimum_however_quiet_the_room()
    {
        var reading = Feed((0.0001f, 100), (0.0029f, 100)).Read();

        reading.OpenRms.ShouldBe(0.003f);
        reading.HeardSpeech.ShouldBeFalse();
    }

    [Fact]
    public void A_noisy_room_raises_the_open_level_so_its_noise_is_no_speech()
    {
        const float Fan = 0.003f; // at the minimum open level on its own

        var reading = Feed((Fan, 3_000)).Read();

        reading.OpenRms.ShouldBe(0.01f, "five times the fan, capped at the maximum");
        reading.HeardSpeech.ShouldBeFalse();
    }

    [Fact]
    public void Speech_over_a_noisy_room_is_speech()
    {
        Feed((0.003f, 200), (Speech, 50)).Read().HeardSpeech.ShouldBeTrue();
    }

    // A clip without any quiet part to measure the room by is capped at the old fixed 0.01: never judged more strictly.
    [Fact]
    public void A_clip_that_is_all_speech_needs_no_more_than_the_maximum()
    {
        var reading = Feed((0.01f, 50)).Read();

        reading.OpenRms.ShouldBe(0.01f);
        reading.HeardSpeech.ShouldBeTrue();
    }

    // Typing while the mic is latched: many loud blocks, but none of them lasts as long as a syllable.
    [Fact]
    public void Key_clicks_are_no_speech_however_many()
    {
        Feed(Repeat(100, (RoomNoise, 20), (Speech, 4))).Read().HeardSpeech.ShouldBeFalse();
    }

    [Fact]
    public void Loud_stretches_of_50_ms_count()
    {
        Feed(Repeat(10, (RoomNoise, 20), (Speech, 5))).Read().HeardSpeech.ShouldBeTrue();
    }

    [Fact]
    public void The_room_noise_is_read_by_time_not_by_block_count()
    {
        var gate = new SpeechGate();
        for (var i = 0; i < 100; i++)
        {
            gate.Step(0.0001f, TimeSpan.Zero); // empty blocks: half the count, none of the time
        }

        for (var i = 0; i < 100; i++)
        {
            gate.Step(0.003f, Block);
        }

        var reading = gate.Read();
        reading.OpenRms.ShouldBe(0.01f);
        reading.HeardSpeech.ShouldBeFalse();
    }

    [Fact]
    public void The_reading_reports_the_loudest_block_and_the_speech_it_counted()
    {
        var reading = Feed((RoomNoise, 50), (QuietSpeech, 30), (Speech, 1)).Read();

        reading.Loudest.ShouldBe(Speech);
        reading.Speech.ShouldBe(TimeSpan.FromMilliseconds(310));
    }

    [Fact]
    public void The_block_duration_is_what_counts_not_the_number_of_blocks()
    {
        var gate = new SpeechGate();
        gate.Step(RoomNoise, TimeSpan.FromMilliseconds(250));

        gate.Step(Speech, TimeSpan.FromMilliseconds(250));
        gate.Read().HeardSpeech.ShouldBeFalse();
        gate.Step(Speech, TimeSpan.FromMilliseconds(250));

        gate.Read().HeardSpeech.ShouldBeTrue();
    }

    [Fact]
    public void Reset_forgets_the_last_clip()
    {
        var gate = Feed((RoomNoise, 50), (Speech, 50));

        gate.Reset();

        gate.Read().HeardSpeech.ShouldBeFalse();
        gate.Read().Loudest.ShouldBe(0f);
        gate.Step(Speech, TimeSpan.FromMilliseconds(490));
        gate.Read().HeardSpeech.ShouldBeFalse();
    }

    [Fact]
    public void A_custom_config_moves_every_limit()
    {
        var gate = new SpeechGate(new SpeechGate.Config
        {
            MinimumOpenRms = 0.1f,
            MaximumOpenRms = 0.2f,
            AboveFloor = 2f,
            MinimumRun = TimeSpan.FromMilliseconds(100),
            MinimumSpeech = TimeSpan.FromMilliseconds(100),
        });

        gate.Step(0.09f, TimeSpan.FromSeconds(1));
        gate.Step(0.2f, TimeSpan.FromMilliseconds(90));
        gate.Read().HeardSpeech.ShouldBeFalse("a 90 ms run is under the minimum run");
        gate.Step(0.2f, TimeSpan.FromMilliseconds(10));

        gate.Read().OpenRms.ShouldBe(0.18f, 1e-6f);
        gate.Read().HeardSpeech.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0.02f, 0.01f, 5f)]
    [InlineData(0f, 0.01f, 5f)]
    [InlineData(float.NaN, 0.01f, 5f)]
    [InlineData(0.003f, 0.01f, 0f)]
    public void A_config_that_cannot_work_is_refused(float minimum, float maximum, float aboveFloor)
    {
        Should.Throw<ArgumentException>(() => new SpeechGate(new SpeechGate.Config
        {
            MinimumOpenRms = minimum,
            MaximumOpenRms = maximum,
            AboveFloor = aboveFloor,
        }));
    }
}
