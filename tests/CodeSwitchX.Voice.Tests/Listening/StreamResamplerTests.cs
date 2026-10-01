using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Listening;

namespace CodeSwitchX.Voice.Tests.Listening;

public sealed class StreamResamplerTests
{
    [Theory]
    [InlineData(48_000, 480)] // RØDE Connect: 10 ms blocks
    [InlineData(44_100, 441)]
    [InlineData(48_000, 137)] // odd block sizes
    [InlineData(16_000, 160)]
    public void Block_by_block_gives_what_the_whole_clip_gives(int rate, int block)
    {
        var input = Enumerable.Range(0, rate).Select(i => (float)Math.Sin(i * 0.01)).ToArray();
        var resampler = new StreamResampler(rate);

        var streamed = new List<float>();
        for (var i = 0; i < input.Length; i += block)
        {
            streamed.AddRange(resampler.Push(input.AsSpan(i, Math.Min(block, input.Length - i))));
        }

        var whole = AudioMath.Resample(input, rate);
        streamed.Count.ShouldBeInRange(whole.Length - 1, whole.Length);
        for (var i = 0; i < streamed.Count; i++)
        {
            streamed[i].ShouldBe(whole[i], 1e-6f);
        }
    }
}
