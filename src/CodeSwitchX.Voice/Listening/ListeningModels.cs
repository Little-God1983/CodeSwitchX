using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CodeSwitchX.Voice.Listening;

/// <summary>Is this 32 ms frame speech? A probability 0..1. Keeps state from frame to frame: one per stream.</summary>
public interface IVoiceActivity : IDisposable
{
    float Step(ReadOnlySpan<float> frame);

    void Reset();
}

/// <summary>Has the user finished their turn, judged from the turn's audio so far? A probability 0..1.</summary>
public interface ITurnEnd : IDisposable
{
    double Complete(ReadOnlySpan<float> turn16k);
}

/// <summary>A model file is in place but will not load: the file is deleted (<see cref="ListeningModelStore.Forget"/>) and
/// downloaded again the next time.</summary>
public sealed class ListeningModelException(ListeningModel model, Exception inner)
    : Exception($"{model.FileName} could not be loaded: {inner.Message}", inner)
{
    public ListeningModel Model { get; } = model;
}

/// <summary>
/// Silero VAD v6 on 512-sample frames at 16 kHz. As Silero's own wrapper (utils_vad.py OnnxWrapper): the model sees the
/// last 64 samples of the previous frame before each frame (576 in all), and its state [2, 1, 128] carries from frame to
/// frame. One thread each way: a frame is a fraction of a millisecond.
/// </summary>
public sealed class SileroVad : IVoiceActivity
{
    public const int FrameSamples = 512;
    private const int Context = 64;

    private readonly InferenceSession _session;
    private readonly float[] _input = new float[Context + FrameSamples];
    private float[] _state = new float[2 * 128];
    private readonly DenseTensor<long> _rate = new(new long[] { 16_000 }, []);

    public SileroVad(string modelPath)
    {
        using var options = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 1 };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0"); // no core spun hot between frames
        _session = new InferenceSession(modelPath, options);
    }

    public float Step(ReadOnlySpan<float> frame)
    {
        if (frame.Length != FrameSamples)
        {
            throw new ArgumentException($"A frame is {FrameSamples} samples.", nameof(frame));
        }

        frame.CopyTo(_input.AsSpan(Context));
        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(_input, [1, _input.Length])),
            NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(_state, [2, 1, 128])),
            NamedOnnxValue.CreateFromTensor("sr", _rate),
        };
        using var results = _session.Run(inputs);
        var probability = results.First(r => r.Name == "output").AsEnumerable<float>().First();
        _state = results.First(r => r.Name == "stateN").AsEnumerable<float>().ToArray();
        _input.AsSpan(FrameSamples, Context).CopyTo(_input); // the frame's last 64 samples are the next one's context
        return probability;
    }

    public void Reset()
    {
        Array.Clear(_input);
        _state = new float[2 * 128];
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>Pipecat's Smart Turn v3.2 on the CPU: the turn's last 8 s as Whisper features in, the probability that the
/// turn is complete out (the output is named "logits" but is already a sigmoid). The model takes about 15 ms, the
/// features 60-90 ms; it runs once per pause. Half the cores, which do not spin waiting for work between calls: an
/// always-on listener must not keep the CPU busy.</summary>
public sealed class SmartTurn : ITurnEnd
{
    private readonly InferenceSession _session;

    public SmartTurn(string modelPath)
    {
        using var options = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        _session = new InferenceSession(modelPath, options);
    }

    public double Complete(ReadOnlySpan<float> turn16k)
    {
        var features = WhisperFeatures.LogMel(turn16k);
        var input = NamedOnnxValue.CreateFromTensor("input_features",
            new DenseTensor<float>(features, [1, WhisperFeatures.Mels, WhisperFeatures.Frames]));
        using var results = _session.Run([input]);
        return results[0].AsEnumerable<float>().First();
    }

    public void Dispose() => _session.Dispose();
}
