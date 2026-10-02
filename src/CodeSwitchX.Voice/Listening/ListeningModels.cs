using CodeSwitchX.Voice.Audio;
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
/// <para>
/// It runs about 31 times a second for as long as Open mic is on, so a frame allocates nothing: the input, the rate, the
/// output and two state buffers are arrays pinned once as OrtValues and bound once to two <see cref="OrtIoBinding"/>s,
/// and a run writes into them. The two state buffers trade roles each frame (one binding reads A and writes B, the other
/// reads B and writes A), so the state is never copied and no name is marshalled per frame.
/// </para>
/// </summary>
public sealed class SileroVad : IVoiceActivity
{
    public const int FrameSamples = 512;
    private const int Context = 64;
    private const int StateSize = 2 * 128;

    private readonly InferenceSession _session;
    private readonly RunOptions _run = new();
    private readonly float[] _input = new float[Context + FrameSamples];
    private readonly float[] _stateA = new float[StateSize];
    private readonly float[] _stateB = new float[StateSize];
    private readonly float[] _output = new float[1];
    private readonly OrtValue[] _values;
    private readonly OrtIoBinding[] _bindings;
    private int _step; // which binding runs next: 0 reads state A, 1 reads state B

    public SileroVad(string modelPath)
    {
        using var options = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 1 };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0"); // no core spun hot between frames
        // A model whose inputs are not named as Silero's makes a binding throw: what was made so far is let go, so the
        // file is not held open and nothing leaks on each start that tries it again.
        var made = new List<IDisposable> { _run };
        try
        {
            _session = new InferenceSession(modelPath, options);
            made.Add(_session);
            var input = Made(OrtValue.CreateTensorValueFromMemory(_input, [1, _input.Length]));
            var rate = Made(OrtValue.CreateTensorValueFromMemory(new long[] { AudioMath.TargetRate }, []));
            var output = Made(OrtValue.CreateTensorValueFromMemory(_output, [1, 1]));
            var stateA = Made(OrtValue.CreateTensorValueFromMemory(_stateA, [2, 1, 128]));
            var stateB = Made(OrtValue.CreateTensorValueFromMemory(_stateB, [2, 1, 128]));
            _values = [input, rate, output, stateA, stateB];
            _bindings = [Bind(stateA, stateB), Bind(stateB, stateA)];

            OrtIoBinding Bind(OrtValue state, OrtValue stateN)
            {
                var binding = Made(_session.CreateIoBinding());
                binding.BindInput("input", input);
                binding.BindInput("state", state);
                binding.BindInput("sr", rate);
                binding.BindOutput("output", output);
                binding.BindOutput("stateN", stateN);
                return binding;
            }
        }
        catch
        {
            for (var i = made.Count - 1; i >= 0; i--)
            {
                made[i].Dispose();
            }

            throw;
        }

        T Made<T>(T value)
            where T : IDisposable
        {
            made.Add(value);
            return value;
        }
    }

    public float Step(ReadOnlySpan<float> frame)
    {
        if (frame.Length != FrameSamples)
        {
            throw new ArgumentException($"A frame is {FrameSamples} samples.", nameof(frame));
        }

        frame.CopyTo(_input.AsSpan(Context));
        _session.RunWithBinding(_run, _bindings[_step]);
        _step ^= 1; // the state just written is the next frame's input
        _input.AsSpan(FrameSamples, Context).CopyTo(_input); // the frame's last 64 samples are the next one's context
        return _output[0];
    }

    public void Reset()
    {
        Array.Clear(_input);
        Array.Clear(_stateA);
        Array.Clear(_stateB);
        _step = 0;
    }

    public void Dispose()
    {
        foreach (var binding in _bindings)
        {
            binding.Dispose();
        }

        foreach (var value in _values)
        {
            value.Dispose();
        }

        _run.Dispose();
        _session.Dispose();
    }
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
