namespace CodeSwitchX.Voice.Tests.Audio;

using CodeSwitchX.Voice.Audio;
using NAudio.Wave;

public sealed class AudioLevelsTests
{
    [Theory]
    [InlineData(100, 1f)]
    [InlineData(0, 0f)]
    [InlineData(50, 0.25f)]
    [InlineData(150, 1f)]
    [InlineData(-5, 0f)]
    public void Gain_of_a_percentage_follows_perceived_loudness(int percent, float gain) =>
        AudioLevels.GainOf(percent).ShouldBe(gain, 1e-6f);

    [Fact]
    public void Defaults_are_sounds_at_60_and_the_voice_at_full()
    {
        var levels = new AudioLevels();

        (levels.Sounds, levels.Voice).ShouldBe((AudioLevels.GainOf(60), 1f));
        AudioLevels.DefaultSoundsPercent.ShouldBe(60);
    }

    [Fact]
    public void Loudness_scales_each_sample_by_the_gain()
    {
        Read(new Loudness(Source(1000, -1000, 32767, -32768), () => 0.5f), 4).ShouldBe([500, -500, 16384, -16384]);
    }

    [Fact]
    public void Loudness_at_full_gain_passes_the_samples_unchanged()
    {
        Read(new Loudness(Source(1000, -1000, 32767, -32768), () => 1f), 4).ShouldBe([1000, -1000, 32767, -32768]);
    }

    [Fact]
    public void Loudness_at_zero_is_silence_of_the_same_length()
    {
        var loudness = new Loudness(Source(1000, -1000, 32767), () => 0f);
        var buffer = new byte[6];

        loudness.Read(buffer, 0, 6).ShouldBe(6);
        buffer.ShouldAllBe(b => b == 0);
    }

    [Fact]
    public void A_gain_changed_between_reads_is_heard_from_the_next_read()
    {
        var gain = 1f;
        var loudness = new Loudness(Source(1000, 1000, 1000, 1000), () => gain);

        var first = Read(loudness, 2);
        gain = 0.5f;
        var second = Read(loudness, 2);

        first.ShouldBe([1000, 1000]);
        second.ShouldBe([500, 500]);
    }

    [Fact]
    public void Loudness_refuses_a_source_that_is_not_16_bit_pcm()
    {
        var floats = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(24000, 1));

        Should.Throw<ArgumentException>(() => new Loudness(floats, () => 1f));
    }

    [Fact]
    public void The_chime_at_the_default_level_is_as_loud_as_before()
    {
        var pcm = ChatChime.Pcm.ToArray();
        var loudness = new Loudness(new RawSourceWaveStream(pcm, 0, pcm.Length, new WaveFormat(24000, 16, 1)), () => AudioLevels.GainOf(AudioLevels.DefaultSoundsPercent));

        var peak = Read(loudness, pcm.Length / 2).Max(s => Math.Abs((int)s));

        ((double)peak).ShouldBe(0.33 * short.MaxValue, 0.05 * 0.33 * short.MaxValue);
    }

    private static IWaveProvider Source(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return new RawSourceWaveStream(bytes, 0, bytes.Length, new WaveFormat(24000, 16, 1));
    }

    private static short[] Read(IWaveProvider provider, int samples)
    {
        var buffer = new byte[samples * 2];
        var read = provider.Read(buffer, 0, buffer.Length);
        var result = new short[read / 2];
        Buffer.BlockCopy(buffer, 0, result, 0, read);
        return result;
    }
}
