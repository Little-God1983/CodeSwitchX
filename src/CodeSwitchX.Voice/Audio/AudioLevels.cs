using System.Runtime.InteropServices;
using NAudio.Wave;

namespace CodeSwitchX.Voice.Audio;

/// <summary>
/// How loud the chats' sounds and Raven's voice play (#305), as gains of 0..1 Settings sets from its sliders. Only what
/// CodeSwitchX plays is scaled: Windows' and the device's volume stay as they are. Any thread.
/// </summary>
public sealed class AudioLevels
{
    /// <summary>The sounds' slider before it is moved: as loud as the chime was before there was a slider.</summary>
    public const int DefaultSoundsPercent = 60;

    /// <summary>The voice's slider before it is moved: full, as loud as Raven spoke before there was a slider.</summary>
    public const int DefaultVoicePercent = 100;

    private float _sounds = GainOf(DefaultSoundsPercent);
    private float _voice = GainOf(DefaultVoicePercent);

    /// <summary>The gain of the chats' sounds.</summary>
    public float Sounds
    {
        get => Volatile.Read(ref _sounds);
        set => Volatile.Write(ref _sounds, value);
    }

    /// <summary>The gain of everything said in Raven's voice: answers, news, the samples.</summary>
    public float Voice
    {
        get => Volatile.Read(ref _voice);
        set => Volatile.Write(ref _voice, value);
    }

    /// <summary>
    /// A slider's percentage as a gain, squared: it follows how loud it sounds, so the lower half of the slider is not
    /// all near silence. Out of 0..100 it is clamped.
    /// </summary>
    public static float GainOf(int percent) => MathF.Pow(Math.Clamp(percent, 0, 100) / 100f, 2);
}

/// <summary>
/// Passes 16-bit PCM through scaled by a gain read on every read: a slider moved is heard at once, also in what already
/// plays.
/// </summary>
internal sealed class Loudness : IWaveProvider
{
    private readonly IWaveProvider _source;
    private readonly Func<float> _gain;

    public Loudness(IWaveProvider source, Func<float> gain)
    {
        if (source.WaveFormat.Encoding != WaveFormatEncoding.Pcm || source.WaveFormat.BitsPerSample != 16)
        {
            throw new ArgumentException($"Only 16-bit PCM is scaled, not {source.WaveFormat}.", nameof(source));
        }

        _source = source;
        _gain = gain;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        var gain = _gain();
        if (gain >= 1f)
        {
            return read;
        }

        // Only ever quieter, so a scaled sample always fits; rounded half away from zero, as -0.5 and 0.5 are alike.
        var samples = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(offset, read - read % 2));
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)MathF.Round(samples[i] * gain, MidpointRounding.AwayFromZero);
        }

        return read;
    }
}
