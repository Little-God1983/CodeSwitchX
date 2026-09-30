namespace CodeSwitchX.Voice.Audio;

/// <summary>What the <see cref="SilentMicWatch"/> reports when the signal state changes.</summary>
public enum SignalEvent
{
    /// <summary>Nothing has ever come through and the grace period is up.</summary>
    Silent,

    /// <summary>The microphone was heard, then delivered nothing for the regrace period (a paused headset is likely).</summary>
    Dropped,

    /// <summary>A signal is back after a <see cref="Silent"/> or <see cref="Dropped"/> report: take the warning down.</summary>
    Live,
}

/// <summary>
/// Is the microphone delivering a signal of any kind? Not "is someone speaking": a virtual device with nothing behind it
/// hands over exact digital zeros and opens without complaint. Ported from ContentAutomatorX dictation.js (signalStep).
/// A working microphone produces no event at all. A signal shorter than the sustain time counts as quiet.
/// </summary>
public sealed class SilentMicWatch
{
    private readonly float floor;
    private readonly TimeSpan grace;
    private readonly TimeSpan regrace;
    private readonly TimeSpan sustain;

    private SignalEvent? warned;
    private bool heard;
    private TimeSpan quiet;
    private TimeSpan sound;

    public SilentMicWatch(Config? config = null)
    {
        config ??= new Config();
        floor = config.Floor;
        grace = config.Grace ?? TimeSpan.FromSeconds(2);
        regrace = config.Regrace ?? TimeSpan.FromSeconds(10);
        sustain = config.Sustain ?? TimeSpan.FromSeconds(0.3);
    }

    /// <summary>
    /// Thresholds. Floor: only digital silence sits under it (real input, even noise-suppressed, stays far above).
    /// Grace: how long nothing may come through before the first warning. Regrace: the same once the microphone has been heard.
    /// Sustain: how long a signal must last to count, so a single click on a dead device is not "heard".
    /// </summary>
    public sealed record Config(float Floor = 1e-6f, TimeSpan? Grace = null, TimeSpan? Regrace = null, TimeSpan? Sustain = null);

    /// <summary>Feeds one captured block. Returns null when nothing is to be reported.</summary>
    public SignalEvent? Step(float rms, TimeSpan blockDuration)
    {
        SignalEvent? result = null;
        sound = rms >= floor ? sound + blockDuration : TimeSpan.Zero;

        if (sound >= sustain)
        {
            quiet = TimeSpan.Zero;
            heard = true;
            if (warned is not null)
            {
                warned = null;
                result = SignalEvent.Live;
            }
        }
        else
        {
            quiet += blockDuration;
            var limit = heard ? regrace : grace;
            if (quiet >= limit && warned is null)
            {
                warned = heard ? SignalEvent.Dropped : SignalEvent.Silent;
                result = warned;
            }
        }

        return result;
    }

    /// <summary>Restores the fresh state, for the start of a new recording.</summary>
    public void Reset()
    {
        warned = null;
        heard = false;
        quiet = TimeSpan.Zero;
        sound = TimeSpan.Zero;
    }
}
