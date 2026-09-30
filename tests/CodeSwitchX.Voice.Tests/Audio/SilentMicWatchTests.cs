namespace CodeSwitchX.Voice.Tests.Audio;

using CodeSwitchX.Voice.Audio;

// Ported from ContentAutomatorX tests/dictation-js/signal.test.js (the signalStep cases).
public sealed class SilentMicWatchTests
{
    private static readonly TimeSpan Block = TimeSpan.FromMilliseconds(100);
    private const float Zero = 0f;        // a virtual device whose software is not running: exact digital silence
    private const float Noise = 0.002f;   // an idle real microphone in a quiet room
    private const float Speech = 0.05f;

    // Enough blocks in a row to count as a signal, not a click: 300 ms.
    private static readonly float[] Sustained = [Speech, Speech, Speech];

    private static List<SignalEvent> Run(SilentMicWatch watch, IEnumerable<float> levels, TimeSpan? block = null)
    {
        var events = new List<SignalEvent>();
        foreach (var level in levels)
        {
            if (watch.Step(level, block ?? Block) is { } e)
            {
                events.Add(e);
            }
        }

        return events;
    }

    private static List<SignalEvent> Run(params IEnumerable<float>[] parts)
    {
        return Run(new SilentMicWatch(), parts.SelectMany(p => p));
    }

    private static float[] Repeat(float level, int count) => Enumerable.Repeat(level, count).ToArray();

    [Fact]
    public void The_defaults_are_two_seconds_of_grace_ten_after_sound_and_a_third_of_a_second_to_count()
    {
        var config = new SilentMicWatch.Config();
        config.Floor.ShouldBeLessThan(Noise);
        config.Floor.ShouldBeGreaterThan(0f);

        // Grace and regrace are pinned by their boundaries: 1.9 s and 9.9 s report nothing, 2 s and 10 s do.
        Run(Repeat(Zero, 19)).ShouldBeEmpty();
        Run(Repeat(Zero, 20)).ShouldBe([SignalEvent.Silent]);
        Run(Sustained, Repeat(Zero, 99)).ShouldBeEmpty();
        Run(Sustained, Repeat(Zero, 100)).ShouldBe([SignalEvent.Dropped]);

        // Two blocks of sound (200 ms) are a click, three (300 ms) are a signal.
        Run(Repeat(Zero, 10), [Speech, Speech], Repeat(Zero, 10)).ShouldBe([SignalEvent.Silent]);
        Run(Repeat(Zero, 10), Sustained, Repeat(Zero, 10)).ShouldBeEmpty();
    }

    [Fact]
    public void Silence_for_the_whole_grace_period_reports_silent_exactly_once()
    {
        Run(Repeat(Zero, 40)).ShouldBe([SignalEvent.Silent]);
    }

    [Fact]
    public void Nothing_is_reported_before_the_grace_period_is_up()
    {
        // A person needs a moment to start talking; 1.9 s of quiet is not a broken microphone.
        Run(Repeat(Zero, 19)).ShouldBeEmpty();
    }

    [Fact]
    public void A_good_microphone_in_a_quiet_room_is_signal_even_after_noise_suppression()
    {
        // White noise at -90 dBFS, one int16 step, comes out of Chromium's default processing at 1.8e-5 RMS.
        const float QuietRoom = 1.8e-5f;
        QuietRoom.ShouldBeGreaterThan(new SilentMicWatch.Config().Floor);
        Run(Repeat(QuietRoom, 40)).ShouldBeEmpty();
    }

    [Fact]
    public void A_working_microphone_reports_nothing_at_all()
    {
        // Its noise floor counts as signal. There is no warning to take down, so no Live event.
        Run(Repeat(Noise, 40)).ShouldBeEmpty();
        Run(Repeat(Speech, 40)).ShouldBeEmpty();
    }

    [Fact]
    public void Sound_after_a_silent_warning_reports_live_so_the_host_can_take_the_warning_down()
    {
        Run(Repeat(Zero, 25), Sustained).ShouldBe([SignalEvent.Silent, SignalEvent.Live]);
    }

    [Fact]
    public void A_click_on_a_dead_device_does_not_count_as_heard_the_full_warning_still_comes()
    {
        Run([Speech], Repeat(Zero, 25)).ShouldBe([SignalEvent.Silent]);
        Run(Repeat(Zero, 10), [Speech, Speech], Repeat(Zero, 15)).ShouldBe([SignalEvent.Silent]);
    }

    [Fact]
    public void A_click_counts_as_quiet_so_it_does_not_push_the_warning_back_either()
    {
        // 20 blocks of 100 ms is the two-second grace, click included.
        Run(Repeat(Zero, 10), [Speech], Repeat(Zero, 9)).ShouldBe([SignalEvent.Silent]);
    }

    [Fact]
    public void A_click_after_the_warning_does_not_take_it_down()
    {
        Run(Repeat(Zero, 25), [Speech, Speech], Repeat(Zero, 5)).ShouldBe([SignalEvent.Silent]);
    }

    [Fact]
    public void Once_sound_has_been_heard_a_pause_gets_ten_seconds_before_anything_is_said()
    {
        // 9.5 s of nothing after speech is a pause for breath, not a dead microphone.
        Run(Sustained, Repeat(Zero, 95)).ShouldBeEmpty();
    }

    [Fact]
    public void A_microphone_that_goes_quiet_for_good_after_sound_is_reported_dropped_not_silent()
    {
        Run(Sustained, Repeat(Zero, 101)).ShouldBe([SignalEvent.Dropped]);
    }

    [Fact]
    public void Sound_after_a_drop_reports_live_again_and_the_next_drop_waits_the_full_ten_seconds_again()
    {
        Run(Sustained, Repeat(Zero, 101), Sustained, Repeat(Zero, 50))
            .ShouldBe([SignalEvent.Dropped, SignalEvent.Live]);
    }

    [Fact]
    public void Brief_digital_silence_between_words_does_not_trip_the_warning()
    {
        var levels = new List<float>();
        for (var i = 0; i < 10; i++)
        {
            levels.AddRange([Speech, Speech, Speech, Speech, Zero, Zero, Zero]);
        }

        Run(levels).ShouldBeEmpty();
    }

    [Fact]
    public void The_grace_period_is_measured_in_time_so_it_survives_a_different_block_size()
    {
        // 20 blocks of 128 ms = 2.56 s.
        Run(new SilentMicWatch(), Repeat(Zero, 20), TimeSpan.FromMilliseconds(128)).ShouldBe([SignalEvent.Silent]);
    }

    [Fact]
    public void A_stretch_with_no_blocks_at_all_counts_as_silence_however_long_it_is()
    {
        var heard = new SilentMicWatch();
        Run(heard, Sustained);
        heard.Step(0f, TimeSpan.FromSeconds(10.5)).ShouldBe(SignalEvent.Dropped);

        new SilentMicWatch().Step(0f, TimeSpan.FromSeconds(2.5)).ShouldBe(SignalEvent.Silent);
    }

    [Fact]
    public void A_custom_config_replaces_the_defaults()
    {
        var watch = new SilentMicWatch(new SilentMicWatch.Config(Grace: TimeSpan.FromSeconds(1)));
        Run(watch, Repeat(Zero, 10)).ShouldBe([SignalEvent.Silent]);
    }

    [Fact]
    public void Reset_starts_a_fresh_recording()
    {
        var watch = new SilentMicWatch();
        Run(watch, Sustained);
        Run(watch, Repeat(Zero, 101)).ShouldBe([SignalEvent.Dropped]);

        watch.Reset();

        // Fresh state: nothing heard yet, so the two-second grace applies again and reports Silent, not Dropped.
        Run(watch, Repeat(Zero, 19)).ShouldBeEmpty();
        Run(watch, Repeat(Zero, 1)).ShouldBe([SignalEvent.Silent]);
    }
}
