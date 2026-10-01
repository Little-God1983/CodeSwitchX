using System.Runtime.InteropServices;
using CodeSwitchX.Voice.Audio;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Voice.Listening;

/// <summary>What a frame told the detector: the user started talking (half a second of speech), or their turn is over.</summary>
public abstract record TurnEvent
{
    public sealed record Started : TurnEvent;

    /// <summary>The turn's audio, 16 kHz: the half second before the speech, the speech, and the first 0.2 s of the pause.</summary>
    public sealed record Ended(float[] Clip) : TurnEvent;
}

/// <summary>
/// Open mic's ear, in two stages as Pipecat pairs them: Silero says which 32 ms frames are speech, and when the speech
/// pauses for 0.2 s Smart Turn says whether the turn is over or the user is only thinking. A pause it calls incomplete
/// is waited out: speech that resumes continues the same turn, and the next pause asks again. A turn ends anyway after
/// 3 s of silence, so a model that never says "complete" (or fails) cannot hold the floor, and at two minutes, the
/// recorder's limit.
/// <para>
/// Silero's own hysteresis: a frame must reach 0.5 to start speech, and 0.35 keeps it going. Speech must add up to half
/// a second before the turn counts (<see cref="TurnEvent.Started"/>): a cough, a key or a knock falls short and is
/// dropped without a trace once 0.2 s of silence follows it.
/// </para>
/// <para>
/// Allocation-free while it waits: the pre-roll is a fixed ring of frames, copied into the turn only when speech starts.
/// </para>
/// <para>
/// Pure: no clock, no I/O. Time is the samples it is fed, so the tests drive it frame by frame. Not thread-safe: one
/// worker feeds it (<see cref="OpenMicListener"/>); <see cref="IgnoreSpeech"/> may be set from any thread.
/// </para>
/// </summary>
public sealed class TurnDetector(IVoiceActivity vad, ITurnEnd turnEnd, ILogger logger)
{
    public const float StartThreshold = 0.5f;
    public const float ContinueThreshold = 0.35f;
    public static readonly TimeSpan MinimumSpeech = TimeSpan.FromSeconds(0.5);
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(0.2);
    public static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan PreRoll = TimeSpan.FromSeconds(0.5);
    public static readonly TimeSpan MaximumTurn = TimeSpan.FromSeconds(120);

    private const int Rate = AudioMath.TargetRate;
    private const int Frame = SileroVad.FrameSamples;
    private static readonly int SmartTurnWindow = 8 * Rate;
    private static readonly int PreRollFrames = FramesIn(PreRoll);

    private readonly float[] _preRoll = new float[PreRollFrames * Frame];
    private int _preRollFirst;
    private int _preRollCount;
    private readonly List<float> _turn = [];
    private volatile bool _ignoreSpeech;
    private bool _inTurn;
    private bool _started;
    private bool _asked;
    private int _speechFrames;
    private int _silentFrames;
    private bool _smartTurnFailed;

    /// <summary>Raven is speaking and the user turned voice barge-in off: no new turn starts, and a burst not yet
    /// started is dropped. A turn already started is the user's and goes on to its real end: they may still be finishing
    /// a sentence when an earlier answer starts to play.</summary>
    public bool IgnoreSpeech
    {
        get => _ignoreSpeech;
        set => _ignoreSpeech = value;
    }

    /// <summary>Feeds one frame of <see cref="SileroVad.FrameSamples"/> samples.</summary>
    public TurnEvent? Step(ReadOnlySpan<float> frame)
    {
        var probability = vad.Step(frame);
        if (_ignoreSpeech && !_started)
        {
            // Raven is talking: a burst that had not become a turn yet is dropped at once, and her voice is not kept as pre-roll.
            Reset(keepVad: true);
            return null;
        }

        var speech = probability >= (_inTurn ? ContinueThreshold : StartThreshold);
        if (!_inTurn)
        {
            if (!speech)
            {
                PushPreRoll(frame);
                return null;
            }

            _inTurn = true;
            for (var i = 0; i < _preRollCount; i++)
            {
                _turn.AddRange(PreRollFrame(i));
            }

            ClearPreRoll();
        }

        _turn.AddRange(frame);
        if (speech)
        {
            _speechFrames++;
            _silentFrames = 0;
            _asked = false;
            if (!_started && _speechFrames >= FramesIn(MinimumSpeech))
            {
                _started = true;
                return new TurnEvent.Started();
            }
        }
        else
        {
            _silentFrames++;
            if (!_started && _silentFrames >= FramesIn(Pause))
            {
                DropBurst(); // a cough, a key: no turn
                return null;
            }

            if (_started && _silentFrames >= FramesIn(GiveUp))
            {
                return End();
            }

            if (_started && !_asked && _silentFrames >= FramesIn(Pause))
            {
                _asked = true;
                if (IsComplete())
                {
                    return End();
                }
            }
        }

        return _turn.Count >= MaximumTurn.TotalSeconds * Rate ? End() : null;
    }

    /// <summary>Back to waiting, with nothing kept: a pause, a mode switch, a new microphone.</summary>
    public void Reset() => Reset(keepVad: false);

    private void Reset(bool keepVad)
    {
        ClearPreRoll();
        ResetTurn(keepVad);
    }

    private void ResetTurn(bool keepVad)
    {
        _turn.Clear();
        _inTurn = false;
        _started = false;
        _asked = false;
        _speechFrames = 0;
        _silentFrames = 0;
        if (!keepVad)
        {
            vad.Reset();
        }
    }

    /// <summary>A cough or a key is forgotten, but the half second before the next speech must still be real audio, so
    /// what the burst swallowed of it is handed back to the pre-roll.</summary>
    private void DropBurst()
    {
        var keep = Math.Min(_turn.Count / Frame, PreRollFrames) * Frame;
        var turn = CollectionsMarshal.AsSpan(_turn);
        ClearPreRoll();
        for (var i = turn.Length - keep; i < turn.Length; i += Frame)
        {
            PushPreRoll(turn.Slice(i, Frame));
        }

        ResetTurn(keepVad: true);
    }

    /// <summary>Keeps the frame as the newest of the pre-roll, over the oldest once the half second is full.</summary>
    private void PushPreRoll(ReadOnlySpan<float> frame)
    {
        int slot;
        if (_preRollCount < PreRollFrames)
        {
            slot = (_preRollFirst + _preRollCount) % PreRollFrames;
            _preRollCount++;
        }
        else
        {
            slot = _preRollFirst;
            _preRollFirst = (_preRollFirst + 1) % PreRollFrames;
        }

        frame.CopyTo(_preRoll.AsSpan(slot * Frame, Frame));
    }

    /// <summary>The pre-roll's frame <paramref name="index"/>, oldest first.</summary>
    private ReadOnlySpan<float> PreRollFrame(int index) =>
        _preRoll.AsSpan((_preRollFirst + index) % PreRollFrames * Frame, Frame);

    private void ClearPreRoll()
    {
        _preRollFirst = 0;
        _preRollCount = 0;
    }

    private bool IsComplete()
    {
        try
        {
            var samples = CollectionsMarshal.AsSpan(_turn);
            return turnEnd.Complete(samples[Math.Max(0, samples.Length - SmartTurnWindow)..]) > 0.5;
        }
        catch (Exception ex)
        {
            if (!_smartTurnFailed)
            {
                _smartTurnFailed = true; // once per listener: every pause would fail the same way
                logger.LogWarning(ex, "Smart Turn failed; Open mic ends turns after {Seconds} s of silence instead", GiveUp.TotalSeconds);
            }

            return false;
        }
    }

    /// <summary>The turn, its silence cut to the 0.2 s pause.</summary>
    private TurnEvent.Ended End()
    {
        var extraSilence = Math.Max(0, _silentFrames - FramesIn(Pause)) * Frame;
        var clip = _turn.GetRange(0, _turn.Count - extraSilence).ToArray();
        Reset(keepVad: true);
        return new TurnEvent.Ended(clip);
    }

    private static int FramesIn(TimeSpan span) => (int)Math.Round(span.TotalSeconds * Rate / Frame);
}
