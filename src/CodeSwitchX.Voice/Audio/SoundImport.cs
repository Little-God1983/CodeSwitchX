using System.Globalization;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CodeSwitchX.Voice.Audio;

/// <summary>A file the user picked that cannot be used as a sound (#307); the message says why in a few words.</summary>
public sealed class SoundImportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Turns a WAV or MP3 of the user's into a sound as the shipped ones are (#307): mono 16-bit PCM at 44.1 kHz, so it plays
/// through the same path at the sounds' level. A file longer than <see cref="MaximumLength"/> is cut there with a short
/// fade-out, so a long file never holds up the sounds behind it.
/// </summary>
public static class SoundImport
{
    public static readonly TimeSpan MaximumLength = TimeSpan.FromSeconds(3);

    public static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(250);

    /// <summary>How much over <see cref="MaximumLength"/> a file may read and still fit: an MP3's padding makes 3 s read 3.04 s.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(50);

    private const int Rate = 44100;

    /// <summary>
    /// Writes the sound made of <paramref name="source"/> to <paramref name="target"/>, and returns how long the source is.
    /// Throws <see cref="SoundImportException"/> for a file that is no WAV or MP3, cannot be read or holds no sound; nothing
    /// is written then.
    /// </summary>
    public static TimeSpan Import(string source, string target)
    {
        float[] samples;
        TimeSpan length;
        try
        {
            using var reader = Open(source);
            length = reader.TotalTime;
            samples = Decode(reader);
        }
        catch (SoundImportException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SoundImportException($"it could not be read ({ex.Message.TrimEnd('.')}).", ex);
        }

        if (samples.Length == 0 || length <= TimeSpan.Zero)
        {
            throw new SoundImportException("it holds no sound.");
        }

        var part = target + ".part";
        using (var writer = new WaveFileWriter(part, new WaveFormat(Rate, 16, 1)))
        {
            foreach (var sample in samples)
            {
                writer.WriteSample(Math.Clamp(sample, -1f, 1f));
            }
        }

        File.Move(part, target, overwrite: true);
        return length;
    }

    /// <summary>What the Sounds page says of a sound longer than <see cref="MaximumLength"/>; null for one that fits.</summary>
    public static string? NoticeFor(TimeSpan length)
    {
        if (!IsLong(length))
        {
            return null;
        }

        var seconds = length.TotalSeconds < 10
            ? length.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)
            : Math.Round(length.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        return $"This sound is {seconds} s long. Only the first {MaximumLength.TotalSeconds:0} s will be used.";
    }

    private static bool IsLong(TimeSpan length) => length > MaximumLength + Slack;

    /// <summary>
    /// A plain PCM or float WAV is read as it is; any other (the extensible header editors write for 24-bit, float and
    /// many-channel files, ADPCM, mu-law) and an MP3 through Media Foundation, which decodes them.
    /// </summary>
    private static WaveStream Open(string source)
    {
        switch (Path.GetExtension(source).ToLowerInvariant())
        {
            case ".wav":
                var wav = new WaveFileReader(source);
                if (wav.WaveFormat.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat)
                {
                    return wav;
                }

                wav.Dispose();
                return new MediaFoundationReader(source);
            case ".mp3":
                return new MediaFoundationReader(source);
            default:
                throw new SoundImportException("only WAV and MP3 files can be added.");
        }
    }

    /// <summary>Mono at 44.1 kHz, up to <see cref="MaximumLength"/>; cut there, the end fades out.</summary>
    private static float[] Decode(WaveStream reader)
    {
        var provider = reader.ToSampleProvider();
        provider = provider.WaveFormat.Channels switch
        {
            1 => provider,
            2 => new StereoToMonoSampleProvider(provider) { LeftVolume = 0.5f, RightVolume = 0.5f },
            _ => new Downmix(provider),
        };
        if (provider.WaveFormat.SampleRate != Rate)
        {
            provider = new WdlResamplingSampleProvider(provider, Rate);
        }

        // A file within the slack is kept whole: cut a few milliseconds short of its end, it would click.
        var most = (int)(Rate * (IsLong(reader.TotalTime) ? MaximumLength : MaximumLength + Slack).TotalSeconds);
        var samples = new float[most];
        var count = 0;
        int read;
        while (count < most && (read = provider.Read(samples, count, most - count)) > 0)
        {
            count += read;
        }

        if (IsLong(reader.TotalTime))
        {
            var fade = Math.Min(count, (int)(Rate * FadeOut.TotalSeconds));
            for (var i = 0; i < fade; i++)
            {
                samples[count - fade + i] *= 1f - (float)(i + 1) / fade;
            }
        }

        return samples[..count];
    }

    /// <summary>More than two channels to one: their average.</summary>
    private sealed class Downmix(ISampleProvider source) : ISampleProvider
    {
        private readonly int _channels = source.WaveFormat.Channels;
        private float[] _buffer = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var needed = count * _channels;
            if (_buffer.Length < needed)
            {
                _buffer = new float[needed];
            }

            var frames = source.Read(_buffer, 0, needed) / _channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < _channels; channel++)
                {
                    sum += _buffer[frame * _channels + channel];
                }

                buffer[offset + frame] = sum / _channels;
            }

            return frames;
        }
    }
}
