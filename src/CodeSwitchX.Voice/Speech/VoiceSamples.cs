using CodeSwitchX.Voice.Audio;
using NAudio.Wave;

namespace CodeSwitchX.Voice.Speech;

/// <summary>
/// The short clips the Voice page plays, one per voice: recorded once with each engine and carried in this assembly, so
/// a voice can be heard before its engine is installed.
/// </summary>
public interface IVoiceSamples
{
    /// <summary>Plays the clip on the output chosen until it ends or <paramref name="ct"/> is cancelled; then returns. Never on the caller's thread.</summary>
    Task PlayAsync(SpeechEngine engine, string voice, CancellationToken ct);
}

/// <summary>
/// Played at the voice's level (#305), as Raven speaks. At 0 % nothing would be heard: no device is woken for it (a sleeping
/// headset stays asleep), but the sample takes its time, as if it played (#310). Raven's own speech plays at 0 all the same:
/// its timing depends on it.
/// </summary>
public sealed class VoiceSamples(IAudioOutput audioOutput, AudioLevels levels, TimeProvider time) : IVoiceSamples
{
    private static string ResourceName(SpeechEngine engine, string voice) => $"CodeSwitchX.Voice.Samples.{engine}.{voice}.wav";

    /// <summary>Whether the voice has a clip; the tests walk every voice offered.</summary>
    internal static bool Has(SpeechEngine engine, string voice) =>
        typeof(VoiceSamples).Assembly.GetManifestResourceInfo(ResourceName(engine, voice)) is not null;

    public Task PlayAsync(SpeechEngine engine, string voice, CancellationToken ct) => Task.Run(async () =>
    {
        await using var clip = typeof(VoiceSamples).Assembly.GetManifestResourceStream(ResourceName(engine, voice))
            ?? throw new InvalidOperationException($"No sample of {engine}'s voice {voice}.");
        await using var reader = new WaveFileReader(clip);
        if (levels.Voice <= 0f)
        {
            try
            {
                await Task.Delay(reader.TotalTime, time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped before its end.
            }

            return;
        }

        using var output = audioOutput.Open(reader, () => levels.Voice, 300);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, _) => ended.TrySetResult();
        output.Play();
        await using (ct.Register(output.Stop))
        {
            await ended.Task.ConfigureAwait(false);
        }
    }, CancellationToken.None);
}
