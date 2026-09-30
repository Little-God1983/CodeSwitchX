namespace CodeSwitchX.Voice.Audio;

/// <summary>Pure sample math for microphone audio (ported from ContentAutomatorX dictation.js).</summary>
public static class AudioMath
{
    public const int TargetRate = 16000;

    public static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0f;
        }

        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * s;
        }

        return (float)Math.Sqrt(sum / samples.Length);
    }

    /// <summary>Maps an RMS value onto 0..1 for a level meter: -60 dB and below is 0, 0 dB is 1.</summary>
    public static double LevelOf(float rms)
    {
        if (!(rms > 0f))
        {
            return 0d;
        }

        var db = 20 * Math.Log10(rms);
        return Math.Clamp((db + 60) / 60, 0d, 1d);
    }

    public static float[] ToMono(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels <= 1)
        {
            return interleaved.ToArray();
        }

        var frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
            {
                sum += interleaved[(f * channels) + c];
            }

            mono[f] = sum / channels;
        }

        return mono;
    }

    /// <summary>Averages windows when down-sampling and holds samples when up-sampling (same as dictation.js resampleTo16k).</summary>
    public static float[] Resample(ReadOnlySpan<float> mono, int fromRate, int toRate = TargetRate)
    {
        if (fromRate == toRate)
        {
            return mono.ToArray();
        }

        var outLength = (int)((long)mono.Length * toRate / fromRate);
        var output = new float[outLength];
        var ratio = (double)fromRate / toRate;
        for (var i = 0; i < outLength; i++)
        {
            var start = (int)(i * ratio);
            var end = Math.Min(mono.Length, Math.Max(start + 1, (int)((i + 1) * ratio)));
            float sum = 0;
            for (var j = start; j < end; j++)
            {
                sum += mono[j];
            }

            output[i] = sum / (end - start);
        }

        return output;
    }
}
