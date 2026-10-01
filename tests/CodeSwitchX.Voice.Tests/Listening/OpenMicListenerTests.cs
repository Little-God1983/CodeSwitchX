using System.Speech.Synthesis;
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Listening;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.Voice.Tests.Listening;

public sealed class OpenMicListenerTests
{
    [Fact]
    public async Task Blocks_of_any_size_reach_the_detector_as_512_sample_frames_and_its_events_come_out()
    {
        var stream = new FakeStream();
        var vad = new LevelVad();
        using var listener = new OpenMicListener(stream, new ListeningModelStore(Path.GetTempPath(), new HttpClient()),
            () => vad, () => new AlwaysComplete(), NullLogger<OpenMicListener>.Instance);
        var started = new TaskCompletionSource();
        var ended = new TaskCompletionSource<float[]>();
        listener.SpeechStarted += (_, _) => started.TrySetResult();
        listener.TurnEnded += (_, clip) => ended.TrySetResult(clip);

        listener.Start("mic");
        stream.Feed(0.5f, seconds: 1.0, block: 441); // speech, in odd blocks
        stream.Feed(0f, seconds: 0.5, block: 441);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var clip = await ended.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        clip.Length.ShouldBeGreaterThan(16_000);
        vad.FrameSizes.ShouldAllBe(n => n == SileroVad.FrameSamples);
        listener.Stop();
        stream.Running.ShouldBeFalse();
    }

    [Fact]
    public async Task A_stop_drops_a_half_spoken_turn_and_a_new_start_begins_fresh()
    {
        var stream = new FakeStream();
        var vad = new LevelVad();
        using var listener = new OpenMicListener(stream, new ListeningModelStore(Path.GetTempPath(), new HttpClient()),
            () => vad, () => new AlwaysComplete(), NullLogger<OpenMicListener>.Instance);
        var ends = 0;
        listener.TurnEnded += (_, _) => Interlocked.Increment(ref ends);

        listener.Start("mic");
        stream.Feed(0.5f, seconds: 1.0, block: 160);
        listener.Stop();
        listener.Start("mic");
        stream.Feed(0f, seconds: 1.0, block: 160);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        ends.ShouldBe(0);
        vad.Resets.ShouldBeGreaterThanOrEqualTo(1);
        listener.Stop();
    }

    [Fact]
    public void A_model_that_will_not_load_is_forgotten_and_reported()
    {
        var folder = Path.Combine(Path.GetTempPath(), "csx-om-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var store = new ListeningModelStore(folder, new HttpClient());
        File.WriteAllBytes(store.PathOf(ListeningModelStore.Silero), [1, 2, 3]);
        using var listener = new OpenMicListener(new FakeStream(), store, NullLogger<OpenMicListener>.Instance);

        var error = Should.Throw<ListeningModelException>(() => listener.Start("mic"));

        error.Model.ShouldBe(ListeningModelStore.Silero);
        File.Exists(store.PathOf(ListeningModelStore.Silero)).ShouldBeFalse();
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task A_handler_that_throws_does_not_stop_later_events()
    {
        var stream = new FakeStream();
        using var listener = new OpenMicListener(stream, new ListeningModelStore(Path.GetTempPath(), new HttpClient()),
            () => new LevelVad(), () => new AlwaysComplete(), NullLogger<OpenMicListener>.Instance);
        var heard = 0;
        var ended = new TaskCompletionSource();
        listener.Heard += (_, _) =>
        {
            Interlocked.Increment(ref heard);
            throw new InvalidOperationException("handler bug");
        };
        listener.TurnEnded += (_, _) => ended.TrySetResult();

        listener.Start("mic");
        stream.Feed(0.5f, seconds: 1.0, block: 160);
        stream.Feed(0f, seconds: 0.5, block: 160);

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        heard.ShouldBeGreaterThan(10);
        listener.Stop();
    }

    [Fact]
    public async Task A_failed_microphone_is_reported_and_the_listener_stops_itself()
    {
        var stream = new FakeStream();
        using var listener = new OpenMicListener(stream, new ListeningModelStore(Path.GetTempPath(), new HttpClient()),
            () => new LevelVad(), () => new AlwaysComplete(), NullLogger<OpenMicListener>.Instance);
        var failed = new TaskCompletionSource<MicrophoneException>();
        listener.Failed += (_, error) => failed.TrySetResult(error);

        listener.Start("mic");
        stream.Running.ShouldBeTrue();
        stream.Fail();

        (await failed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Kind.ShouldBe(MicrophoneFailureKind.Missing);
        for (var i = 0; i < 100 && stream.Running; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        stream.Running.ShouldBeFalse();
        listener.Stop(); // a second stop is harmless
    }

    [Fact]
    public void A_turn_end_model_that_will_not_load_disposes_the_voice_activity_model_that_did()
    {
        var vad = new LevelVad();
        var folder = Path.Combine(Path.GetTempPath(), "csx-om-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var store = new ListeningModelStore(folder, new HttpClient());
        using var listener = new OpenMicListener(new FakeStream(), store, () => vad,
            () => throw new InvalidOperationException("no"), NullLogger<OpenMicListener>.Instance);

        var error = Should.Throw<ListeningModelException>(() => listener.Start("mic"));

        error.Model.ShouldBe(ListeningModelStore.SmartTurn);
        vad.Disposed.ShouldBeTrue();
        Directory.Delete(folder, recursive: true);
    }

    // Needs the models in %LOCALAPPDATA%\CodeSwitchX\models\listening.
    [Fact(Explicit = true)]
    public async Task A_sentence_with_a_one_second_pause_in_the_middle_is_one_turn()
    {
        var store = new ListeningModelStore(Path.Combine(CodeSwitchX.Core.AppPaths.Default().ModelsDirectory, "listening"), new HttpClient());
        var path = Path.Combine(Path.GetTempPath(), "csx-open-mic-pause.pcm");
        var audio = Speak("I would like to open the") .Concat(new float[16_000]).Concat(Speak("Diffusion Nexus workspace, please.")).Concat(new float[3 * 16_000]).ToArray();
        WritePcm(path, audio);
        using var listener = new OpenMicListener(new FileMicrophoneStream(path, realTime: false), store, NullLogger<OpenMicListener>.Instance);
        var turns = new List<float[]>();
        listener.TurnEnded += (_, clip) => { lock (turns) { turns.Add(clip); } };

        listener.Start("file");
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        listener.Stop();

        lock (turns)
        {
            turns.Count.ShouldBe(1, "the pause mid-sentence must not end the turn");
            (turns[0].Length / 16_000.0).ShouldBeGreaterThan(3.0);
        }
    }

    private static float[] Speak(string text)
    {
        using var stream = new MemoryStream();
        using (var synth = new SpeechSynthesizer())
        {
            synth.SetOutputToAudioStream(stream, new System.Speech.AudioFormat.SpeechAudioFormatInfo(16_000,
                System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
            synth.Speak(text);
        }

        var bytes = stream.ToArray();
        var samples = new float[bytes.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        }

        return samples;
    }

    private static void WritePcm(string path, float[] audio)
    {
        var bytes = new byte[audio.Length * 2];
        for (var i = 0; i < audio.Length; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), (short)Math.Clamp(audio[i] * 32767f, short.MinValue, short.MaxValue));
        }

        File.WriteAllBytes(path, bytes);
    }

    private sealed class FakeStream : IMicrophoneStream
    {
        public bool Running { get; private set; }

        public event EventHandler<CapturedFrames>? FramesCaptured;

        public event EventHandler<MicrophoneException>? Failed;

        public void Start(string deviceId) => Running = true;

        public void Stop() => Running = false;

        public void Feed(float level, double seconds, int block)
        {
            var total = (int)(seconds * 16_000);
            for (var i = 0; i < total; i += block)
            {
                var samples = new float[Math.Min(block, total - i)];
                Array.Fill(samples, level);
                FramesCaptured?.Invoke(this, new CapturedFrames(samples, level));
            }
        }

        public void Fail() => Failed?.Invoke(this, new MicrophoneException(MicrophoneFailureKind.Missing, "gone", new Exception()));
    }

    /// <summary>Speech is any frame whose first sample is loud.</summary>
    private sealed class LevelVad : IVoiceActivity
    {
        public List<int> FrameSizes { get; } = [];

        public int Resets { get; private set; }

        public float Step(ReadOnlySpan<float> frame)
        {
            lock (FrameSizes)
            {
                FrameSizes.Add(frame.Length);
            }

            return frame[0] > 0.1f ? 0.9f : 0.05f;
        }

        public bool Disposed { get; private set; }

        public void Reset() => Resets++;

        public void Dispose() => Disposed = true;
    }

    private sealed class AlwaysComplete : ITurnEnd
    {
        public double Complete(ReadOnlySpan<float> turn16k) => 0.9;

        public void Dispose()
        {
        }
    }
}
