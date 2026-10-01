using CodeSwitchX.Voice.Audio;

namespace CodeSwitchX.Voice.Listening;

/// <summary>
/// <see cref="AudioMath.Resample"/> one block at a time: output sample i averages input samples [⌊i·r⌋, ⌊(i+1)·r⌋), r
/// being from/to, counted over everything pushed so far, so block edges change nothing. Input not yet covered by a whole
/// output sample waits for the next block.
/// </summary>
public sealed class StreamResampler(int fromRate)
{
    private readonly double _ratio = (double)fromRate / AudioMath.TargetRate;
    private readonly List<float> _pending = [];
    private long _pendingStart; // the absolute index of _pending[0]
    private long _next; // the next output sample's index

    public float[] Push(ReadOnlySpan<float> mono)
    {
        if (fromRate == AudioMath.TargetRate)
        {
            return mono.ToArray();
        }

        _pending.AddRange(mono);
        var available = _pendingStart + _pending.Count;
        var output = new List<float>();
        while (true)
        {
            var start = (long)(_next * _ratio);
            var end = Math.Max(start + 1, (long)((_next + 1) * _ratio));
            if (end > available)
            {
                break;
            }

            float sum = 0;
            for (var j = start; j < end; j++)
            {
                sum += _pending[(int)(j - _pendingStart)];
            }

            output.Add(sum / (end - start));
            _next++;
        }

        var keepFrom = (long)(_next * _ratio);
        var drop = (int)Math.Clamp(keepFrom - _pendingStart, 0, _pending.Count);
        _pending.RemoveRange(0, drop);
        _pendingStart += drop;
        return [.. output];
    }
}
