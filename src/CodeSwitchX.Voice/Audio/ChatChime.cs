using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace CodeSwitchX.Voice.Audio;

/// <summary>The short sound another chat makes instead of speaking (#125); the tests use a fake.</summary>
public interface IChatChime
{
    /// <summary>Plays it once on the output chosen, off the caller's thread. Any thread; never throws.</summary>
    void Play();
}

/// <summary>
/// Two soft rising tones, a quarter of a second in all, made here rather than shipped as a file. Each play opens the
/// output chosen, or the default device of the moment, and closes it when done, on the thread pool: opening a waking device can take a while.
/// </summary>
public sealed class ChatChime : IChatChime
{
    private const int SampleRate = 24000;

    private static readonly byte[] Pcm = Make();

    private readonly IAudioOutput _audioOutput;
    private readonly ILogger<ChatChime> _logger;

    public ChatChime(IAudioOutput audioOutput, ILogger<ChatChime> logger)
    {
        _audioOutput = audioOutput;
        _logger = logger;
    }

    public void Play() => _ = Task.Run(PlayNow);

    private void PlayNow()
    {
        IWavePlayer? output = null;
        try
        {
            output = _audioOutput.Open(new RawSourceWaveStream(Pcm, 0, Pcm.Length, new WaveFormat(SampleRate, 16, 1)), 120);
            var playing = output;
            output.PlaybackStopped += (_, _) => playing.Dispose();
            output.Play();
        }
        catch (Exception ex)
        {
            output?.Dispose();
            _logger.LogWarning(ex, "Playing the chat chime failed");
        }
    }

    /// <summary>880 Hz then 1320 Hz, each faded in and out so neither clicks, at a third of full loudness.</summary>
    private static byte[] Make()
    {
        (double Hz, double Seconds)[] tones = [(880, 0.11), (1320, 0.14)];
        var samples = new List<short>();
        foreach (var (hz, seconds) in tones)
        {
            var count = (int)(SampleRate * seconds);
            for (var i = 0; i < count; i++)
            {
                var fade = Math.Min(1, Math.Min(i, count - i) / (SampleRate * 0.012));
                samples.Add((short)(Math.Sin(2 * Math.PI * hz * i / SampleRate) * fade * 0.33 * short.MaxValue));
            }
        }

        var bytes = new byte[samples.Count * 2];
        Buffer.BlockCopy(samples.ToArray(), 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
