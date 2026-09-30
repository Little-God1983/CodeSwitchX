namespace CodeSwitchX.Voice.Tests.Audio;

using CodeSwitchX.Voice.Audio;

public sealed class SpeechGateTests
{
    private static readonly TimeSpan Block = TimeSpan.FromMilliseconds(10); // what WASAPI hands over on the RØDE input
    private const float RoomNoise = 0.0004f;  // the loudest block of three seconds of a quiet room on the RØDE input
    private const float Speech = 0.05f;

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

    [Fact]
    public void The_defaults_are_an_open_level_of_0_01_and_half_a_second_of_speech()
    {
        var config = new SpeechGate.Config();

        config.OpenRms.ShouldBe(0.01f);
        config.MinimumSpeech.ShouldBe(TimeSpan.FromSeconds(0.5));
    }

    [Fact]
    public void A_fresh_gate_has_heard_nothing()
    {
        new SpeechGate().HeardSpeech.ShouldBeFalse();
    }

    [Fact]
    public void Room_noise_alone_is_no_speech_however_long_it_lasts()
    {
        Feed((RoomNoise, 12_000)).HeardSpeech.ShouldBeFalse();
    }

    [Fact]
    public void Half_a_second_of_speech_is_speech()
    {
        Feed((Speech, 50)).HeardSpeech.ShouldBeTrue();
    }

    [Fact]
    public void Just_under_half_a_second_of_speech_is_not()
    {
        Feed((Speech, 49)).HeardSpeech.ShouldBeFalse();
    }

    [Fact]
    public void Speech_broken_up_by_pauses_adds_up()
    {
        Feed((Speech, 20), (RoomNoise, 30), (Speech, 20), (RoomNoise, 30), (Speech, 10)).HeardSpeech.ShouldBeTrue();
    }

    [Fact]
    public void A_block_exactly_at_the_open_level_counts()
    {
        Feed((0.01f, 50)).HeardSpeech.ShouldBeTrue();
    }

    [Fact]
    public void The_block_duration_is_what_counts_not_the_number_of_blocks()
    {
        var gate = new SpeechGate();

        gate.Step(Speech, TimeSpan.FromMilliseconds(250));
        gate.HeardSpeech.ShouldBeFalse();
        gate.Step(Speech, TimeSpan.FromMilliseconds(250));

        gate.HeardSpeech.ShouldBeTrue();
    }

    [Fact]
    public void Reset_forgets_the_speech_of_the_last_clip()
    {
        var gate = Feed((Speech, 50));

        gate.Reset();

        gate.HeardSpeech.ShouldBeFalse();
        gate.Step(Speech, TimeSpan.FromMilliseconds(490));
        gate.HeardSpeech.ShouldBeFalse();
    }

    [Fact]
    public void A_custom_config_moves_both_limits()
    {
        var gate = new SpeechGate(new SpeechGate.Config { OpenRms = 0.1f, MinimumSpeech = TimeSpan.FromMilliseconds(100) });

        gate.Step(0.09f, TimeSpan.FromSeconds(1));
        gate.HeardSpeech.ShouldBeFalse();
        gate.Step(0.1f, TimeSpan.FromMilliseconds(100));

        gate.HeardSpeech.ShouldBeTrue();
    }
}
