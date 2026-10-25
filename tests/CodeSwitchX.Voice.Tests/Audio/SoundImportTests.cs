namespace CodeSwitchX.Voice.Tests.Audio;

using System.Runtime.InteropServices;
using CodeSwitchX.Voice.Audio;
using NAudio.Wave;

/// <summary>#307: a WAV or MP3 of the user's becomes a sound as the shipped ones are: 16-bit mono at 44.1 kHz, at most 3 s.</summary>
public sealed class SoundImportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("csx-import-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Target => Path.Combine(_dir, "out.wav");

    /// <summary>A 440 Hz tone of <paramref name="seconds"/> at <paramref name="peak"/>, in every channel of <paramref name="format"/>.</summary>
    private string Tone(string name, WaveFormat format, double seconds, float peak = 0.5f)
    {
        var path = Path.Combine(_dir, name);
        using var writer = new WaveFileWriter(path, format);
        var count = (int)(format.SampleRate * seconds);
        for (var i = 0; i < count; i++)
        {
            var sample = peak * MathF.Sin(2 * MathF.PI * 440 * i / format.SampleRate);
            for (var channel = 0; channel < format.Channels; channel++)
            {
                writer.WriteSample(sample);
            }
        }

        return path;
    }

    private static (WaveFormat Format, short[] Samples) Read(string path)
    {
        using var reader = new WaveFileReader(path);
        var bytes = new byte[reader.Length];
        reader.ReadExactly(bytes);
        return (reader.WaveFormat, MemoryMarshal.Cast<byte, short>(bytes).ToArray());
    }

    private static double Peak(IEnumerable<short> samples) => samples.Max(s => Math.Abs((int)s)) / (double)short.MaxValue;

    [Fact]
    public void A_stereo_float_wav_at_48_khz_becomes_16_bit_mono_at_44_1_khz()
    {
        var source = Tone("float.wav", WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), 1.0);

        SoundImport.Import(source, Target).TotalSeconds.ShouldBe(1.0, 0.01);

        var (format, samples) = Read(Target);
        (format.Encoding, format.BitsPerSample, format.Channels, format.SampleRate).ShouldBe((WaveFormatEncoding.Pcm, 16, 1, 44100));
        ((double)samples.Length).ShouldBe(44100, 100);
        Peak(samples).ShouldBe(0.5, 0.01, "as loud as it was");
    }

    /// <summary>
    /// Review of #307: the extensible header many editors write for 24-bit, float and many-channel files, at 48 kHz.
    /// </summary>
    [Theory]
    [InlineData(24, 2)]
    [InlineData(24, 6)]
    public void An_extensible_wav_is_read(int bits, int channels)
    {
        var source = Path.Combine(_dir, $"extensible-{bits}-{channels}.wav");
        var format = new WaveFormatExtensible(48000, bits, channels);
        using (var writer = new WaveFileWriter(source, format))
        {
            for (var i = 0; i < 48000 / 2; i++)
            {
                var value = (int)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 48000) * 8_388_607);
                for (var channel = 0; channel < channels; channel++)
                {
                    writer.Write([(byte)value, (byte)(value >> 8), (byte)(value >> 16)], 0, 3);
                }
            }
        }

        SoundImport.Import(source, Target).TotalSeconds.ShouldBe(0.5, 0.01);

        var (written, samples) = Read(Target);
        (written.Channels, written.SampleRate).ShouldBe((1, 44100));
        Peak(samples).ShouldBe(0.5, 0.02);
    }

    /// <summary>Review of #307: a sound trimmed to 3 s, which its encoder's padding makes read a little longer, is no long sound.</summary>
    [Fact]
    public void A_sound_a_few_milliseconds_over_three_seconds_is_no_long_sound()
    {
        var source = Tone("trimmed.wav", new WaveFormat(44100, 16, 1), 3.04);

        var length = SoundImport.Import(source, Target);

        SoundImport.NoticeFor(length).ShouldBeNull();
        Peak(Read(Target).Samples[^220..]).ShouldBeGreaterThan(0.4, "not faded: nothing of it was cut that the ear would miss");
    }

    [Fact]
    public void A_24_bit_wav_is_read()
    {
        var source = Tone("deep.wav", new WaveFormat(44100, 24, 1), 0.5);

        SoundImport.Import(source, Target);

        Peak(Read(Target).Samples).ShouldBe(0.5, 0.01);
    }

    [Fact]
    public void An_mp3_is_read()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Audio", "Fixtures", "tone.mp3"); // a second of 440 Hz at half scale

        SoundImport.Import(source, Target).TotalSeconds.ShouldBe(1.0, 0.1);

        var (format, samples) = Read(Target);
        (format.Channels, format.SampleRate).ShouldBe((1, 44100));
        Peak(samples).ShouldBe(0.5, 0.05);
    }

    [Fact]
    public void A_file_over_three_seconds_is_cut_with_a_fade()
    {
        var source = Tone("long.wav", new WaveFormat(44100, 16, 1), 12);

        SoundImport.Import(source, Target).TotalSeconds.ShouldBe(12, 0.01);

        var samples = Read(Target).Samples;
        ((double)samples.Length).ShouldBe(3 * 44100, 10);
        Peak(samples[^220..]).ShouldBeLessThan(0.02, "faded out, not cut off");
        Peak(samples[(int)(2.5 * 44100)..(int)(2.6 * 44100)]).ShouldBeGreaterThan(0.4, "loud until the fade");
    }

    [Theory]
    [InlineData("text.wav")]
    [InlineData("empty.wav")]
    [InlineData("silent.wav")]
    [InlineData("tune.ogg")]
    public void A_file_that_is_no_sound_is_refused_and_nothing_is_written(string name)
    {
        var source = Path.Combine(_dir, name);
        switch (name)
        {
            case "text.wav":
                File.WriteAllText(source, "not a sound at all");
                break;
            case "empty.wav":
                File.WriteAllBytes(source, []);
                break;
            case "silent.wav":
                Tone(name, new WaveFormat(44100, 16, 1), 0);
                break;
            default:
                File.WriteAllBytes(source, [1, 2, 3, 4]);
                break;
        }

        Should.Throw<SoundImportException>(() => SoundImport.Import(source, Target)).Message.ShouldNotBeNullOrWhiteSpace();
        File.Exists(Target).ShouldBeFalse();
    }

    [Fact]
    public void The_notice_says_the_length()
    {
        SoundImport.NoticeFor(TimeSpan.FromSeconds(12.4)).ShouldBe("This sound is 12 s long. Only the first 3 s will be used.");
        SoundImport.NoticeFor(TimeSpan.FromSeconds(3.24)).ShouldBe("This sound is 3.2 s long. Only the first 3 s will be used.");
        SoundImport.NoticeFor(TimeSpan.FromSeconds(3)).ShouldBeNull();
    }
}
