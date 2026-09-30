namespace CodeSwitchX.Voice.Audio;

/// <summary>What <see cref="SpeechGate.Read"/> found in the clip.</summary>
/// <param name="OpenRms">The level a block had to reach to count as speech in this clip.</param>
/// <param name="Loudest">The loudest block of the clip.</param>
/// <param name="Speech">How much of the clip, added up, reached <paramref name="OpenRms"/>.</param>
public sealed record SpeechReading(bool HeardSpeech, float OpenRms, float Loudest, TimeSpan Speech);

/// <summary>
/// Did anyone speak during the clip? Whisper makes up words on a clip with nobody talking ("Oh.", "Thank you.", the
/// vocabulary prompt read back), and on the low noise of a quiet room it can also retry its decoding for seconds. So a
/// clip that never got loud enough for long enough is not transcribed at all. Speech is counted across the whole clip,
/// not in one run: people pause between words.
/// <para>
/// How loud is loud enough depends on the input. ContentAutomatorX gets away with a fixed 0.02 because the browser's gain
/// control lifts quiet input before it looks; WASAPI hands over the raw level, and normal speech on the RØDE Connect
/// Virtual Input measured only 0.007–0.01. So the open level follows the clip's own room noise instead: eight times the
/// quietest tenth of its blocks (the pauses between words), kept between a minimum just above a quiet room and the old
/// fixed maximum.
/// </para>
/// </summary>
public sealed class SpeechGate
{
    /// <summary>Where in the clip's blocks, sorted by level, the room noise is read: the quietest tenth.</summary>
    private const double FloorPercentile = 0.1;

    private readonly Config config;
    private readonly List<(float Rms, TimeSpan Duration)> blocks = [];

    public SpeechGate(Config? config = null)
    {
        this.config = config ?? new Config();
    }

    /// <summary>
    /// MinimumOpenRms: the open level in the quietest room. A quiet room on the RØDE Connect input measured 0.0002–0.0004
    /// typical (2026-09-30), so 0.002 is still five times its loudest.
    /// MaximumOpenRms: the open level in a noisy room, and the fixed level this gate used before (half ContentAutomatorX's
    /// 0.02): no clip is judged more strictly than that.
    /// AboveFloor: how far above the clip's room noise a block must be to count, 8 times being 18 dB.
    /// MinimumSpeech: how much of the clip, added up, must reach the open level (ContentAutomatorX's minSpeech).
    /// </summary>
    public sealed record Config
    {
        public float MinimumOpenRms { get; init; } = 0.002f;

        public float MaximumOpenRms { get; init; } = 0.01f;

        public float AboveFloor { get; init; } = 8f;

        public TimeSpan MinimumSpeech { get; init; } = TimeSpan.FromSeconds(0.5);
    }

    /// <summary>Feeds one captured block.</summary>
    public void Step(float rms, TimeSpan blockDuration) => blocks.Add((rms, blockDuration));

    /// <summary>Judges the blocks fed since the last reset. Only the whole clip tells its room noise, so this is read at its end.</summary>
    public SpeechReading Read()
    {
        if (blocks.Count == 0)
        {
            return new SpeechReading(false, config.MinimumOpenRms, 0f, TimeSpan.Zero);
        }

        var levels = new float[blocks.Count];
        for (var i = 0; i < levels.Length; i++)
        {
            levels[i] = blocks[i].Rms;
        }

        Array.Sort(levels);
        var floor = levels[(int)(levels.Length * FloorPercentile)];
        var open = Math.Clamp(floor * config.AboveFloor, config.MinimumOpenRms, config.MaximumOpenRms);

        var speech = TimeSpan.Zero;
        foreach (var (rms, duration) in blocks)
        {
            if (rms >= open)
            {
                speech += duration;
            }
        }

        return new SpeechReading(speech >= config.MinimumSpeech, open, levels[^1], speech);
    }

    /// <summary>Restores the fresh state, for the start of a new recording.</summary>
    public void Reset() => blocks.Clear();
}
