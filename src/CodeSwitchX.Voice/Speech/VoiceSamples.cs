using CodeSwitchX.Voice.Audio;
using NAudio.Wave;

namespace CodeSwitchX.Voice.Speech;

/// <summary>
/// The short clips the Voice page plays, one per voice: recorded once with each engine and carried in this assembly, so
/// a voice can be heard before its engine is installed.
/// </summary>
public interface IVoiceSamples
{
    /// <summary>
    /// Plays the clip on the output chosen until it ends or <paramref name="ct"/> is cancelled; then returns. At voice 0 % it opens
    /// no output and only takes the clip's length (#310); stopped before it began, it opens none either. A stop while a waking
    /// device opens returns at once, and the device is let go unplayed; a play outlasting the clip by a margin is stopped (#318).
    /// Never on the caller's thread.
    /// </summary>
    Task PlayAsync(SpeechEngine engine, string voice, CancellationToken ct);
}

/// <summary>
/// Played at the voice's level (#305), as Raven speaks. At 0 % nothing would be heard: no device is woken for it (a sleeping
/// headset stays asleep), but the sample takes its time, as if it played (#310). Raven's own speech plays at 0 all the same:
/// its timing depends on it.
/// </summary>
public sealed class VoiceSamples(IAudioOutput audioOutput, AudioLevels levels, TimeProvider time) : IVoiceSamples
{
    /// <summary>
    /// How much longer than its clip a sample may play once its device is open (#318): past it the play is stopped, so a device
    /// that never says it stopped does not leave the row "playing" for good. A headset's wake in the open comes before it, and is not
    /// counted; one that only starts to sound a moment after the play has the margin for it.
    /// </summary>
    public static readonly TimeSpan PlayMargin = TimeSpan.FromSeconds(5);

    private static string ResourceName(SpeechEngine engine, string voice) => $"CodeSwitchX.Voice.Samples.{engine}.{voice}.wav";

    /// <summary>Whether the voice has a clip; the tests walk every voice offered.</summary>
    internal static bool Has(SpeechEngine engine, string voice) =>
        typeof(VoiceSamples).Assembly.GetManifestResourceInfo(ResourceName(engine, voice)) is not null;

    public Task PlayAsync(SpeechEngine engine, string voice, CancellationToken ct) => Task.Run(async () =>
    {
        if (ct.IsCancellationRequested)
        {
            return; // stopped before it began (the slider let go twice in a row): no device is woken for it
        }

        if (levels.Voice <= 0f)
        {
            try
            {
                await Task.Delay(LengthOf(engine, voice), time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped before its end.
            }

            return;
        }

        var opening = Task.Run(() =>
        {
            var clip = Clip(engine, voice);
            WaveFileReader? reader = null;
            try
            {
                reader = new WaveFileReader(clip); // which does not close the clip
                return new Opened(clip, reader, audioOutput.Open(reader, () => levels.Voice, 300));
            }
            catch
            {
                reader?.Dispose();
                clip.Dispose();
                throw;
            }
        }, CancellationToken.None);
        Opened opened;
        try
        {
            // A headset waking can hold the open for seconds (#318): a stop does not wait for it, and the device is let go once open.
            opened = await opening.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _ = opening.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    t.Result.Close();
                }

                _ = t.Exception; // an open that failed after the stop: nothing to tell
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return;
        }

        try
        {
            if (ct.IsCancellationRequested)
            {
                return; // stopped just as the device opened: closed unheard
            }

            // A device that never says it stopped does not leave the sample playing for good (#318); counted from here, so a
            // headset's wake takes none of it.
            using var capped = new CancellationTokenSource(opened.Reader.TotalTime + PlayMargin, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, capped.Token);

            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            opened.Player.PlaybackStopped += (_, _) => ended.TrySetResult();
            opened.Player.Play();
            await using (linked.Token.Register(() => ended.TrySetResult()))
            {
                await ended.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            opened.Close();
        }
    }, CancellationToken.None);

    /// <summary>A sample's device, open on its clip.</summary>
    private sealed class Opened(Stream clip, WaveFileReader reader, IWavePlayer player)
    {
        public WaveFileReader Reader => reader;

        public IWavePlayer Player => player;

        /// <summary>Stops the device and lets it go, then the clip.</summary>
        public void Close()
        {
            AudioOutput.DisposeQuietly(player);
            reader.Dispose();
            clip.Dispose();
        }
    }

    private static Stream Clip(SpeechEngine engine, string voice) =>
        typeof(VoiceSamples).Assembly.GetManifestResourceStream(ResourceName(engine, voice))
        ?? throw new InvalidOperationException($"No sample of {engine}'s voice {voice}.");

    /// <summary>How long the clip plays; read and let go before a silent sample waits it out.</summary>
    internal static TimeSpan LengthOf(SpeechEngine engine, string voice)
    {
        using var clip = Clip(engine, voice);
        using var reader = new WaveFileReader(clip);
        return reader.TotalTime;
    }
}
