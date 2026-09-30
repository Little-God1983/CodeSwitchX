namespace CodeSwitchX.Voice.Tests.Audio;

using CodeSwitchX.Voice.Audio;

public sealed class AudioMathTests
{
    [Fact]
    public void The_rms_of_a_full_scale_square_wave_is_one()
    {
        AudioMath.Rms([1f, -1f, 1f, -1f]).ShouldBe(1f, 1e-6f);
    }

    [Fact]
    public void The_rms_of_nothing_is_zero()
    {
        AudioMath.Rms([]).ShouldBe(0f);
    }

    [Theory]
    [InlineData(1f, 1d)]        // 0 dB
    [InlineData(0.001f, 0d)]    // -60 dB is the floor
    [InlineData(0f, 0d)]
    [InlineData(0.0316228f, 0.5d)] // -30 dB is half way
    public void The_level_maps_minus_60_to_0_db_onto_0_to_1(float rms, double level)
    {
        AudioMath.LevelOf(rms).ShouldBe(level, 0.001);
    }

    [Theory]
    [InlineData(3f, 1d)]
    [InlineData(-0.5f, 0d)]
    [InlineData(float.NaN, 0d)]
    public void The_level_never_leaves_0_to_1_even_for_clipped_or_invalid_blocks(float rms, double level)
    {
        AudioMath.LevelOf(rms).ShouldBe(level);
    }

    [Fact]
    public void The_level_is_a_decibel_scale_so_quiet_speech_still_moves_the_bar()
    {
        var speech = AudioMath.LevelOf(0.05f);
        speech.ShouldBeInRange(0.5, 0.7);

        var noise = AudioMath.LevelOf(0.002f);
        noise.ShouldBeGreaterThan(0d);
        noise.ShouldBeLessThan(0.15);

        AudioMath.LevelOf(0.5f).ShouldBeGreaterThan(speech);
    }

    [Fact]
    public void Stereo_is_averaged_to_mono()
    {
        AudioMath.ToMono([1f, 0f, 0.5f, 0.5f], channels: 2).ShouldBe([0.5f, 0.5f]);
    }

    [Fact]
    public void Mono_input_is_copied_unchanged()
    {
        AudioMath.ToMono([0.1f, 0.2f], channels: 1).ShouldBe([0.1f, 0.2f]);
    }

    [Fact]
    public void Resampling_48k_to_16k_keeps_one_third_of_the_samples_and_the_dc_level()
    {
        var input = Enumerable.Repeat(0.25f, 48000).ToArray();
        var output = AudioMath.Resample(input, 48000);
        output.Length.ShouldBe(16000);
        output.ShouldAllBe(s => Math.Abs(s - 0.25f) < 1e-4f);
    }

    [Fact]
    public void Resampling_at_the_target_rate_returns_a_copy()
    {
        AudioMath.Resample([0.1f, 0.2f, 0.3f], 16000).ShouldBe([0.1f, 0.2f, 0.3f]);
    }
}
