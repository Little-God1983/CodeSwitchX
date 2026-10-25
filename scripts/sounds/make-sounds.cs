// Makes the sounds CodeSwitchX ships (#306): musical, short, 16-bit PCM mono at 44.1 kHz, written to
// src/CodeSwitchX.Voice/Audio/Sounds. Run from the repository root: dotnet run scripts/sounds/make-sounds.cs
// Everything is synthesised here, so the files are free of anyone's rights (CC0 1.0, see SOURCES.md there).

const int Rate = 44100;
var outDir = args.Length > 0 ? args[0] : Path.Combine("src", "CodeSwitchX.Voice", "Audio", "Sounds");
Directory.CreateDirectory(outDir);
var random = new Random(306); // the same files every run

// Marimba: a bright attack whose upper partials die fast, the fundamental a little slower.
(double Ratio, double Amp, double Decay)[] marimba = [(1, 1, 1), (3.93, 0.35, 3), (9.2, 0.1, 6)];
// Kalimba: a metal tine, its overtones off the harmonic series.
(double Ratio, double Amp, double Decay)[] kalimba = [(1, 1, 1), (5.4, 0.25, 4), (13.3, 0.08, 8)];
// A small bell: inharmonic partials that ring on.
(double Ratio, double Amp, double Decay)[] bell = [(1, 1, 1), (2.76, 0.5, 1.5), (5.4, 0.25, 2.5), (8.93, 0.12, 4)];

const double C4 = 261.63, Eb4 = 311.13, G4 = 392.00, A4 = 440.00, C5 = 523.25, E5 = 659.25, G5 = 783.99, A5 = 880.00, B5 = 987.77,
    D6 = 1174.66, E6 = 1318.51, A6 = 1760.00;

Write("marimba-rise", Mix(1.0, (0, Mallet(C5, 0.7)), (0.09, Mallet(E5, 0.7)), (0.18, Mallet(G5, 0.8))));
Write("marimba-fall", Mix(1.1, (0, Mallet(G4, 0.7)), (0.13, Mallet(Eb4, 0.7)), (0.26, Mallet(C4, 0.85))));
Write("marimba-single", Mix(0.7, (0, Mallet(A5, 0.7))));
Write("kalimba-up", Mix(1.0, (0, Tine(D6, 0.8)), (0.12, Tine(A6, 0.85))));
Write("kalimba-single", Mix(0.8, (0, Tine(E6, 0.8))));
Write("bell-soft", Mix(1.4, (0, Note(G5, 1.4, bell, 2.5))));
Write("bell-pair", Mix(1.4, (0, Note(E5, 1.2, bell, 2.8)), (0.18, Note(B5, 1.2, bell, 2.8))));
Write("wood-knock", Mix(0.18, (0, Knock())));
Write("wood-double", Mix(0.3, (0, Knock()), (0.12, Knock())));
Write("pluck-high", Mix(0.35, (0, Pluck(A6, 0.35))), peak: 0.25);
Write("pluck-low", Mix(0.8, (0, Pluck(A4, 0.8))));
Write("two-tones", Mix(0.25, (0, Tone(880, 0.11)), (0.11, Tone(1320, 0.14))));
Console.WriteLine($"Wrote 12 sounds to {Path.GetFullPath(outDir)}");

double[] Mallet(double hz, double seconds)
{
    var note = Note(hz, seconds, marimba, 6);
    AddNoise(note, 0.003, 0.15); // the mallet's tap
    return note;
}

double[] Tine(double hz, double seconds)
{
    var note = Note(hz, seconds, kalimba, 4);
    AddNoise(note, 0.004, 0.3); // the thumb leaving the tine
    return note;
}

// Partials at ratios of the fundamental, each fading exponentially at the base decay times its own factor.
double[] Note(double hz, double seconds, (double Ratio, double Amp, double Decay)[] partials, double decay)
{
    var samples = new double[(int)(Rate * seconds)];
    for (var i = 0; i < samples.Length; i++)
    {
        var t = (double)i / Rate;
        var v = 0.0;
        foreach (var (ratio, amp, factor) in partials)
        {
            if (hz * ratio < Rate / 2.0)
            {
                v += amp * Math.Sin(2 * Math.PI * hz * ratio * t) * Math.Exp(-t * decay * factor);
            }
        }

        samples[i] = v * Math.Min(1, t / 0.002); // 2 ms in: no click
    }

    return samples;
}

void AddNoise(double[] into, double seconds, double amp)
{
    var count = (int)(Rate * seconds);
    for (var i = 0; i < count && i < into.Length; i++)
    {
        into[i] += amp * (random.NextDouble() * 2 - 1) * (1 - (double)i / count);
    }
}

// A dry wooden knock: noise through a narrow band-pass at 900 Hz, and a low partial that dies in 40 ms.
double[] Knock()
{
    var samples = new double[(int)(Rate * 0.15)];
    var w0 = 2 * Math.PI * 900 / Rate;
    var alpha = Math.Sin(w0) / (2 * 8);
    double a0 = 1 + alpha, a1 = -2 * Math.Cos(w0), a2 = 1 - alpha;
    double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
    var burst = (int)(Rate * 0.025);
    for (var i = 0; i < samples.Length; i++)
    {
        var x = i < burst ? (random.NextDouble() * 2 - 1) * (1 - (double)i / burst) : 0;
        var y = (alpha * x - alpha * x2 - a1 * y1 - a2 * y2) / a0;
        (x2, x1, y2, y1) = (x1, x, y1, y);
        var t = (double)i / Rate;
        samples[i] = 4 * y + 0.5 * Math.Sin(2 * Math.PI * 600 * t) * Math.Exp(-t / 0.04) * Math.Min(1, t / 0.001);
    }

    return samples;
}

// A plucked string (Karplus-Strong): a burst of noise fed back through an averaging delay line.
double[] Pluck(double hz, double seconds)
{
    var period = (int)Math.Round(Rate / hz);
    var line = new double[period];
    for (var i = 0; i < period; i++)
    {
        line[i] = random.NextDouble() * 2 - 1;
    }

    var samples = new double[(int)(Rate * seconds)];
    for (var i = 0; i < samples.Length; i++)
    {
        var at = i % period;
        var next = (at + 1) % period;
        samples[i] = line[at];
        line[at] = 0.996 * 0.5 * (line[at] + line[next]);
    }

    return samples;
}

// The old chime's tone: a sine with 12 ms fades.
double[] Tone(double hz, double seconds)
{
    var samples = new double[(int)(Rate * seconds)];
    for (var i = 0; i < samples.Length; i++)
    {
        var fade = Math.Min(1, Math.Min(i, samples.Length - i) / (Rate * 0.012));
        samples[i] = Math.Sin(2 * Math.PI * hz * i / Rate) * fade;
    }

    return samples;
}

double[] Mix(double seconds, params (double At, double[] Samples)[] parts)
{
    var mixed = new double[(int)(Rate * seconds)];
    foreach (var (at, part) in parts)
    {
        var start = (int)(Rate * at);
        for (var i = 0; i < part.Length && start + i < mixed.Length; i++)
        {
            mixed[start + i] += part[i];
        }
    }

    return mixed;
}

// Faded out over its last 30 ms, normalised to the peak, and written as a 16-bit mono WAV.
void Write(string id, double[] samples, double peak = 0.9)
{
    var fade = (int)(Rate * 0.03);
    for (var i = 0; i < fade; i++)
    {
        samples[samples.Length - 1 - i] *= (double)i / fade;
    }

    var max = samples.Max(Math.Abs);
    using var file = new BinaryWriter(File.Create(Path.Combine(outDir, id + ".wav")));
    var data = samples.Length * 2;
    file.Write("RIFF"u8);
    file.Write(36 + data);
    file.Write("WAVEfmt "u8);
    file.Write(16);
    file.Write((short)1); // PCM
    file.Write((short)1); // mono
    file.Write(Rate);
    file.Write(Rate * 2);
    file.Write((short)2);
    file.Write((short)16);
    file.Write("data"u8);
    file.Write(data);
    foreach (var s in samples)
    {
        file.Write((short)Math.Round(s / max * peak * short.MaxValue));
    }
}
