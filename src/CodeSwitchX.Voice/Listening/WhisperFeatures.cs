namespace CodeSwitchX.Voice.Listening;

/// <summary>
/// The Whisper log-mel features Smart Turn v3 reads: 80 mel bins by 800 frames of the last 8 s of 16 kHz audio, padded
/// with zeros at the front when shorter. A port of Pipecat's numpy code (pipecat/audio/turn/smart_turn/
/// _whisper_features.py at 3b2dccd, itself the math of transformers' WhisperFeatureExtractor): the waveform is
/// normalised to zero mean and unit variance, framed with a periodic Hann window (n_fft 400, hop 160, reflect-padded by
/// 200 at both ends), turned into a power spectrum, projected on Slaney mel filters, log10'd, the last frame dropped,
/// clamped to 8 under the maximum and mapped by (x + 4) / 4. Tested against Pipecat's output (WhisperFeaturesTests).
/// <para>
/// The 400-point transform is a plain DFT on precomputed tables: 800 frames of 201 bins is some 64 million
/// multiply-adds, tens of milliseconds, once per pause in the user's speech.
/// </para>
/// </summary>
public static class WhisperFeatures
{
    public const int Mels = 80;
    public const int Frames = 800;
    public const int Samples = 8 * Rate;

    private const int Rate = 16_000;
    private const int Fft = 400;
    private const int Hop = 160;
    private const int Bins = (Fft / 2) + 1;

    private static readonly double[] Window = HannWindow();
    private static readonly double[] Cos = Table(Math.Cos);
    private static readonly double[] Sin = Table(Math.Sin);
    private static readonly double[,] MelFilters = BuildMelFilters();

    public static float[] LogMel(ReadOnlySpan<float> audio16k)
    {
        // The last 8 s, zeros in front (local_smart_turn_v3.py truncate_audio_to_last_n_seconds).
        var x = new float[Samples];
        var take = Math.Min(audio16k.Length, Samples);
        audio16k[^take..].CopyTo(x.AsSpan(Samples - take));
        Normalize(x);

        var padded = new double[Samples + Fft];
        for (var i = 0; i < padded.Length; i++)
        {
            padded[i] = x[Reflect(i - (Fft / 2), Samples)];
        }

        var log = new double[Mels, Frames];
        var power = new double[Bins];
        var max = double.NegativeInfinity;
        for (var frame = 0; frame < Frames; frame++) // the reference makes 801 and drops the last
        {
            var start = frame * Hop;
            for (var k = 0; k < Bins; k++)
            {
                double re = 0, im = 0;
                for (var n = 0; n < Fft; n++)
                {
                    var v = padded[start + n] * Window[n];
                    var index = (k * n) % Fft;
                    re += v * Cos[index];
                    im -= v * Sin[index];
                }

                power[k] = (re * re) + (im * im);
            }

            for (var m = 0; m < Mels; m++)
            {
                double sum = 0;
                for (var k = 0; k < Bins; k++)
                {
                    sum += MelFilters[k, m] * power[k];
                }

                var value = Math.Log10(Math.Max(1e-10, sum));
                log[m, frame] = value;
                max = Math.Max(max, value);
            }
        }

        var features = new float[Mels * Frames];
        for (var m = 0; m < Mels; m++)
        {
            for (var frame = 0; frame < Frames; frame++)
            {
                features[(m * Frames) + frame] = (float)((Math.Max(log[m, frame], max - 8.0) + 4.0) / 4.0);
            }
        }

        return features;
    }

    /// <summary>Zero mean, unit variance (numpy's x.var() + 1e-7 under the root), as transformers' do_normalize.</summary>
    private static void Normalize(float[] x)
    {
        double mean = 0;
        foreach (var v in x)
        {
            mean += v;
        }

        mean /= x.Length;
        double variance = 0;
        foreach (var v in x)
        {
            variance += (v - mean) * (v - mean);
        }

        variance /= x.Length;
        var scale = 1.0 / Math.Sqrt(variance + 1e-7);
        for (var i = 0; i < x.Length; i++)
        {
            x[i] = (float)((x[i] - mean) * scale);
        }
    }

    /// <summary>numpy's "reflect" padding: the edge sample is not repeated.</summary>
    private static int Reflect(int i, int length) => i < 0 ? -i : i >= length ? (2 * (length - 1)) - i : i;

    private static double[] HannWindow()
    {
        var window = new double[Fft];
        for (var n = 0; n < Fft; n++)
        {
            window[n] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * n / Fft)); // periodic: np.hanning(401)[:-1]
        }

        return window;
    }

    private static double[] Table(Func<double, double> f)
    {
        var table = new double[Fft];
        for (var i = 0; i < Fft; i++)
        {
            table[i] = f(2 * Math.PI * i / Fft);
        }

        return table;
    }

    /// <summary>Slaney-scale triangular filters with Slaney area normalisation, [bin, mel] as the reference builds them.</summary>
    private static double[,] BuildMelFilters()
    {
        var melMin = HertzToMel(0);
        var melMax = HertzToMel(Rate / 2.0);
        var filterFreqs = new double[Mels + 2];
        for (var i = 0; i < filterFreqs.Length; i++)
        {
            filterFreqs[i] = MelToHertz(melMin + ((melMax - melMin) * i / (Mels + 1)));
        }

        var filters = new double[Bins, Mels];
        for (var k = 0; k < Bins; k++)
        {
            var fft = (Rate / 2.0) * k / (Bins - 1);
            for (var m = 0; m < Mels; m++)
            {
                var down = (fft - filterFreqs[m]) / (filterFreqs[m + 1] - filterFreqs[m]);
                var up = (filterFreqs[m + 2] - fft) / (filterFreqs[m + 2] - filterFreqs[m + 1]);
                var enorm = 2.0 / (filterFreqs[m + 2] - filterFreqs[m]);
                filters[k, m] = Math.Max(0, Math.Min(down, up)) * enorm;
            }
        }

        return filters;
    }

    private static double HertzToMel(double hz) =>
        hz >= 1000 ? 15.0 + (Math.Log(hz / 1000.0) * (27.0 / Math.Log(6.4))) : 3.0 * hz / 200.0;

    private static double MelToHertz(double mel) =>
        mel >= 15.0 ? 1000.0 * Math.Exp((Math.Log(6.4) / 27.0) * (mel - 15.0)) : 200.0 * mel / 3.0;
}
