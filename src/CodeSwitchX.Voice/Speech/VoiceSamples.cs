using NAudio.Wave;

namespace CodeSwitchX.Voice.Speech;

/// <summary>
/// The short clips the Voice page plays, one per voice: recorded once with each engine and carried in this assembly, so
/// a voice can be heard before its engine is installed.
/// </summary>
public interface IVoiceSamples
{
    /// <summary>Plays the clip on the default output device until it ends or <paramref name="ct"/> is cancelled; then returns. Never on the caller's thread.</summary>
    Task PlayAsync(SpeechEngine engine, string voice, CancellationToken ct);
}

public sealed class VoiceSamples : IVoiceSamples
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
        using var output = new WaveOutEvent();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, _) => ended.TrySetResult();
        output.Init(reader);
        output.Play();
        await using (ct.Register(output.Stop))
        {
            await ended.Task.ConfigureAwait(false);
        }
    }, CancellationToken.None);
}
