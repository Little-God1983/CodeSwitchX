namespace CodeSwitchX.Voice.Audio;

/// <summary>What <see cref="SpeechGate.Read"/> found in the clip.</summary>
/// <param name="OpenRms">The level a block had to reach to count as speech in this clip.</param>
/// <param name="Loudest">The loudest block of the clip.</param>
/// <param name="Speech">How much of the clip, added up, counted as speech.</param>
public sealed record SpeechReading(bool HeardSpeech, float OpenRms, float Loudest, TimeSpan Speech);

/// <summary>
/// Did anyone speak during the clip? Whisper makes up words on a clip with nobody talking ("Oh.", "Thank you.", the
/// vocabulary prompt read back), and on the low noise of a quiet room it can also retry its decoding for seconds. So a
/// clip that never got loud enough for long enough is not transcribed at all. Speech is counted across the whole clip,
/// not in one run: people pause between words.
/// <para>
/// How loud is loud enough depends on the input. ContentAutomatorX gets away with a fixed 0.02 because the browser's gain
/// control lifts quiet input before it looks; WASAPI hands over the raw level, and normal speech on the RØDE Connect
/// Virtual Input measured only 0.007–0.01. So the open level follows the clip's own room noise: five times its quietest
/// twentieth (by time), kept between a minimum above a quiet room and the old fixed maximum. The quietest twentieth is
/// the lead-in before the first word and the tail after the last; in a clip with neither it is the gaps between words,
/// which sit about 15 dB under the words, so five times (14 dB) is still under them. Digital silence (a Bluetooth headset
/// waking up, a virtual input whose source has not started) is no room and is left out of the floor.
/// </para>
/// <para>
/// Only loud stretches of at least <see cref="Config.MinimumRun"/> count: a syllable lasts longer than that, a key click
/// or a knock on the desk does not.
/// </para>
/// </summary>
public sealed class SpeechGate
{
    /// <summary>What share of the clip's time, quietest first, the room noise is read from.</summary>
    private const double FloorShare = 0.05;

    /// <summary>Only digital silence sits under this (as in <see cref="SilentMicWatch"/>): no room noise at all.</summary>
    private const float DigitalSilence = 1e-6f;

    private readonly Config config;
    private readonly List<(float Rms, TimeSpan Duration)> blocks = [];

    public SpeechGate(Config? config = null)
    {
        this.config = config ?? new Config();
        if (!(this.config.MinimumOpenRms > 0f) || this.config.MaximumOpenRms < this.config.MinimumOpenRms)
        {
            throw new ArgumentException("The open levels must be positive, the maximum no lower than the minimum.", nameof(config));
        }

        if (!(this.config.AboveFloor > 0f))
        {
            throw new ArgumentException("AboveFloor must be positive.", nameof(config));
        }
    }

    /// <summary>
    /// MinimumOpenRms: the open level in the quietest room. A quiet room on the RØDE Connect input measured 0.0002–0.0004
    /// typical (2026-09-30), so 0.003 is still more than seven times its loudest, and under the quietest speech measured.
    /// MaximumOpenRms: the open level in a noisy room, and the fixed level this gate used before (half ContentAutomatorX's
    /// 0.02): no clip is judged more strictly than that.
    /// AboveFloor: how far above the clip's room noise a block must be to count, 5 times being 14 dB.
    /// MinimumRun: how long a block must stay at the open level, with its neighbours, to count.
    /// MinimumSpeech: how much of the clip, added up, must count (ContentAutomatorX's minSpeech).
    /// </summary>
    public sealed record Config
    {
        public float MinimumOpenRms { get; init; } = 0.003f;

        public float MaximumOpenRms { get; init; } = 0.01f;

        public float AboveFloor { get; init; } = 5f;

        public TimeSpan MinimumRun { get; init; } = TimeSpan.FromMilliseconds(50);

        public TimeSpan MinimumSpeech { get; init; } = TimeSpan.FromSeconds(0.5);
    }

    /// <summary>Feeds one captured block.</summary>
    public void Step(float rms, TimeSpan blockDuration) => blocks.Add((rms, blockDuration));

    /// <summary>Judges the blocks fed since the last reset. Only the whole clip tells its room noise, so this is read at its end.</summary>
    public SpeechReading Read()
    {
        var open = Math.Clamp(RoomNoise() * config.AboveFloor, config.MinimumOpenRms, config.MaximumOpenRms);
        var loudest = 0f;
        var speech = TimeSpan.Zero;
        var run = TimeSpan.Zero;
        foreach (var (rms, duration) in blocks)
        {
            loudest = Math.Max(loudest, rms);
            if (rms >= open)
            {
                run += duration;
                continue;
            }

            speech += Counted(run);
            run = TimeSpan.Zero;
        }

        speech += Counted(run);
        return new SpeechReading(speech >= config.MinimumSpeech, open, loudest, speech);
    }

    /// <summary>Restores the fresh state, for the start of a new recording.</summary>
    public void Reset() => blocks.Clear();

    private TimeSpan Counted(TimeSpan run) => run >= config.MinimumRun ? run : TimeSpan.Zero;

    /// <summary>The level of the clip's quietest twentieth by time, digital silence left out; 0 for a clip with none.</summary>
    private float RoomNoise()
    {
        var heard = blocks.Where(b => b.Rms >= DigitalSilence).OrderBy(b => b.Rms).ToList();
        var total = TimeSpan.Zero;
        foreach (var block in heard)
        {
            total += block.Duration;
        }

        var share = total * FloorShare;
        var quietest = TimeSpan.Zero;
        foreach (var (rms, duration) in heard)
        {
            quietest += duration;
            if (quietest > share)
            {
                return rms;
            }
        }

        return heard.Count > 0 ? heard[^1].Rms : 0f;
    }
}
