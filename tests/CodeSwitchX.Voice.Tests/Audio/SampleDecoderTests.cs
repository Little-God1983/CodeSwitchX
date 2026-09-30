namespace CodeSwitchX.Voice.Tests.Audio;

using CodeSwitchX.Voice.Audio;
using NAudio.Wave;

public sealed class SampleDecoderTests
{
    private static byte[] Bytes(params float[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    [Fact]
    public void Stereo_float_frames_are_averaged_to_mono()
    {
        var buffer = Bytes(1f, 0f, -1f, -1f);
        SampleDecoder.ToMonoFloats(buffer, buffer.Length, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))
            .ShouldBe([0.5f, -1f]);
    }

    [Fact]
    public void Sixteen_bit_pcm_is_scaled_to_minus_one_to_one()
    {
        byte[] buffer = [0x00, 0x00, 0xFF, 0x7F, 0x00, 0x80];
        SampleDecoder.ToMonoFloats(buffer, buffer.Length, new WaveFormat(16000, 16, 1))
            .ShouldBe([0f, 32767f / 32768f, -1f]);
    }

    [Fact]
    public void Twenty_four_bit_pcm_is_scaled_to_minus_one_to_one()
    {
        byte[] buffer = [0xFF, 0xFF, 0x7F];
        var result = SampleDecoder.ToMonoFloats(buffer, buffer.Length, new WaveFormat(16000, 24, 1));
        result.Length.ShouldBe(1);
        result[0].ShouldBe(1f, 1e-6f);
    }

    [Fact]
    public void Thirty_two_bit_pcm_is_scaled_to_minus_one_to_one()
    {
        byte[] buffer = [0x00, 0x00, 0x00, 0x80];
        SampleDecoder.ToMonoFloats(buffer, buffer.Length, new WaveFormat(16000, 32, 1))
            .ShouldBe([-1f]);
    }

    [Fact]
    public void An_extensible_format_with_a_float_subformat_is_read_as_float()
    {
        var format = new WaveFormatExtensible(48000, 32, 2);
        var buffer = Bytes(0.25f, 0.75f);
        SampleDecoder.ToMonoFloats(buffer, buffer.Length, format).ShouldBe([0.5f]);
    }

    [Fact]
    public void Only_the_recorded_part_of_the_buffer_is_decoded()
    {
        var buffer = Bytes(1f, 1f, 1f, 1f);
        SampleDecoder.ToMonoFloats(buffer, 2 * sizeof(float), WaveFormat.CreateIeeeFloatWaveFormat(16000, 1))
            .ShouldBe([1f, 1f]);
    }
}
