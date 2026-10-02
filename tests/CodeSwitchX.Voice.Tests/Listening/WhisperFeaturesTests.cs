using CodeSwitchX.Voice.Dictation;
using CodeSwitchX.Voice.Listening;

namespace CodeSwitchX.Voice.Tests.Listening;

public sealed class WhisperFeaturesTests
{
    [Fact]
    public void The_features_of_the_warm_up_sample_match_Pipecats()
    {
        var expected = Fixture("warmup-features.bin");

        var features = WhisperFeatures.LogMel(WarmUpSpeech.Load());

        features.Length.ShouldBe(WhisperFeatures.Mels * WhisperFeatures.Frames);
        var worst = 0f;
        for (var i = 0; i < features.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(features[i] - expected[i]));
        }

        worst.ShouldBeLessThan(2e-3f, "the port must compute what Smart Turn was trained on");
    }

    [Fact]
    public void Only_the_last_eight_seconds_count()
    {
        var sample = WarmUpSpeech.Load();
        var longer = new float[WhisperFeatures.Samples + 16_000];
        sample.CopyTo(longer, longer.Length - sample.Length);

        WhisperFeatures.LogMel(longer).ShouldBe(WhisperFeatures.LogMel(sample));
    }

    [Fact]
    public void Silence_gives_finite_features()
    {
        WhisperFeatures.LogMel(new float[16_000]).ShouldAllBe(f => float.IsFinite(f));
    }

    private static float[] Fixture(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Listening", "Fixtures", name));
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}
