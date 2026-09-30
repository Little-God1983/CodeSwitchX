namespace CodeSwitchX.Voice.Audio;

/// <summary>
/// Did anyone speak during the clip? Whisper makes up words on a clip with nobody talking ("Oh.", "Thank you.", the
/// vocabulary prompt read back), and on the low noise of a quiet room it can also retry its decoding for seconds. So a
/// clip that never got loud enough for long enough is not transcribed at all. Speech is counted across the whole clip,
/// not in one run: people pause between words. Starts from ContentAutomatorX's vadConfig (openRms 0.02, minSpeech 0.5 s).
/// </summary>
public sealed class SpeechGate
{
    private readonly float openRms;
    private readonly TimeSpan minimumSpeech;
    private TimeSpan speech;

    public SpeechGate(Config? config = null)
    {
        config ??= new Config();
        openRms = config.OpenRms;
        minimumSpeech = config.MinimumSpeech;
    }

    /// <summary>
    /// OpenRms: the level a block must reach to count as speech. ContentAutomatorX uses 0.02; this is half that, because
    /// dropping a softly spoken clip is worse than transcribing an empty one. A quiet room on the RØDE Connect input
    /// measured 0.00012 typical and 0.0004 at its loudest block (2026-09-30), so 0.01 is still 25 times above it.
    /// MinimumSpeech: how much of the clip, added up, must reach that level.
    /// </summary>
    public sealed record Config
    {
        public float OpenRms { get; init; } = 0.01f;

        public TimeSpan MinimumSpeech { get; init; } = TimeSpan.FromSeconds(0.5);
    }

    /// <summary>At least <see cref="Config.MinimumSpeech"/> of the blocks fed since the last reset reached the open level.</summary>
    public bool HeardSpeech => speech >= minimumSpeech;

    /// <summary>Feeds one captured block.</summary>
    public void Step(float rms, TimeSpan blockDuration)
    {
        if (rms >= openRms)
        {
            speech += blockDuration;
        }
    }

    /// <summary>Restores the fresh state, for the start of a new recording.</summary>
    public void Reset() => speech = TimeSpan.Zero;
}
