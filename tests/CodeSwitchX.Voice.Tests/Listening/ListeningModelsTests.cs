using System.Diagnostics;
using CodeSwitchX.Voice.Dictation;
using CodeSwitchX.Voice.Listening;

namespace CodeSwitchX.Voice.Tests.Listening;

/// <summary>Needs the models in %LOCALAPPDATA%\CodeSwitchX\models\listening (switch to Open mic once, or run
/// ListeningModelStore.DownloadAsync). Alone, not beside other tests: the timing test measures the CPU.</summary>
[Collection(nameof(ListeningModelsCollection))]
public sealed class ListeningModelsTests
{
    private static string PathOf(ListeningModel model) =>
        Path.Combine(CodeSwitchX.Core.AppPaths.Default().ModelsDirectory, "listening", model.FileName);

    [Fact(Explicit = true)]
    public void Silero_hears_speech_in_the_warm_up_sample_and_none_in_silence()
    {
        using var vad = new SileroVad(PathOf(ListeningModelStore.Silero));
        var speech = WarmUpSpeech.Load();

        var heard = Frames(speech).Max(f => vad.Step(f));
        vad.Reset();
        var quiet = Frames(new float[16_000]).Max(f => vad.Step(f));

        heard.ShouldBeGreaterThan(0.8f);
        quiet.ShouldBeLessThan(0.2f);
    }

    [Fact(Explicit = true)]
    public void After_a_reset_Silero_hears_the_same_speech_exactly_as_the_first_time()
    {
        using var vad = new SileroVad(PathOf(ListeningModelStore.Silero));
        var frames = Frames(WarmUpSpeech.Load()).Take(31).ToList(); // an odd count: the reset falls on the second state buffer

        var first = frames.Select(f => vad.Step(f)).ToList();
        vad.Reset();
        var again = frames.Select(f => vad.Step(f)).ToList();

        again.ShouldBe(first);
    }

    [Fact(Explicit = true)]
    public void A_Silero_frame_allocates_nothing()
    {
        using var vad = new SileroVad(PathOf(ListeningModelStore.Silero));
        var frames = Frames(WarmUpSpeech.Load()).Take(64).ToList();
        foreach (var frame in frames.Take(8))
        {
            vad.Step(frame); // the first runs pay for the session's set-up
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var frame in frames)
        {
            vad.Step(frame);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0);
    }

    [Fact(Explicit = true)]
    public void Smart_turn_answers_in_well_under_a_frame_budget()
    {
        using var turn = new SmartTurn(PathOf(ListeningModelStore.SmartTurn));
        var speech = WarmUpSpeech.Load();
        turn.Complete(speech); // the first call pays for the session's set-up

        var clock = Stopwatch.StartNew();
        var p = turn.Complete(speech);
        clock.Stop();

        p.ShouldBeInRange(0, 1);
        clock.ElapsedMilliseconds.ShouldBeLessThan(500, $"SmartTurn.Complete took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void A_model_file_that_is_not_a_model_fails_with_its_own_exception()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            Should.Throw<Microsoft.ML.OnnxRuntime.OnnxRuntimeException>(() => new SileroVad(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Fourth review of #89: a model with other input names lets go of all it made
    [Fact(Explicit = true)]
    public void A_valid_model_with_other_inputs_fails_as_Silero_and_lets_go_of_the_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"not-silero-{Guid.NewGuid():N}.onnx");
        File.Copy(PathOf(ListeningModelStore.SmartTurn), path); // a real model, whose input is not Silero's "input"
        try
        {
            for (var i = 0; i < 3; i++)
            {
                Should.Throw<Microsoft.ML.OnnxRuntime.OnnxRuntimeException>(() => new SileroVad(path));
            }
        }
        finally
        {
            File.Delete(path); // throws if the failed constructor still held the file
        }

        File.Exists(path).ShouldBeFalse();
    }

    private static IEnumerable<float[]> Frames(float[] audio)
    {
        for (var i = 0; i + SileroVad.FrameSamples <= audio.Length; i += SileroVad.FrameSamples)
        {
            yield return audio[i..(i + SileroVad.FrameSamples)];
        }
    }
}

/// <summary>The model tests run with no other test beside them.</summary>
[CollectionDefinition(nameof(ListeningModelsCollection), DisableParallelization = true)]
public sealed class ListeningModelsCollection;
