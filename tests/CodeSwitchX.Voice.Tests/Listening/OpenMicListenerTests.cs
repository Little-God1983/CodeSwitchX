using System.Speech.Synthesis;
using CodeSwitchX.Tests;
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
        listener.TurnEnded += (_, turn) => ended.TrySetResult(turn.Clip);

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
    public async Task What_is_heard_comes_in_batches_whose_durations_add_up_to_the_audio()
    {
        var stream = new FakeStream();
        using var listener = new OpenMicListener(stream, new ListeningModelStore(Path.GetTempPath(), new HttpClient()),
            () => new LevelVad(), () => new AlwaysComplete(), NullLogger<OpenMicListener>.Instance);
        var batches = new List<CapturedBlock>();
        listener.Heard += (_, batch) => { lock (batches) { batches.Add(batch); } };

        listener.Start("mic");
        stream.Feed(0.01f, seconds: 1.0, block: 160); // 100 blocks of 10 ms
        await Task.Delay(300, TestContext.Current.CancellationToken);
        listener.Stop();

        lock (batches)
        {
            batches.Count.ShouldBeLessThanOrEqualTo(25);
            batches.Sum(b => b.Duration.Ticks).ShouldBe(TimeSpan.FromSeconds(1).Ticks);
            batches.ShouldAllBe(b => b.Rms == 0.01f);
        }
    }

    [Fact]
    public async Task A_detector_that_fails_mid_turn_ends_the_turn_with_an_empty_clip()
    {
        var stream = new FakeStream();
        var vad = new LevelVad();
        using var listener = new OpenMicListener(stream, new ListeningModelStore(Path.GetTempPath(), new HttpClient()),
            () => vad, () => new AlwaysComplete(), NullLogger<OpenMicListener>.Instance);
        var started = new TaskCompletionSource();
        var ended = new TaskCompletionSource<float[]>();
        listener.SpeechStarted += (_, _) =>
        {
            vad.ThrowOnce = true; // the next frame fails, while the turn is under way
            started.TrySetResult();
        };
        listener.TurnEnded += (_, turn) => ended.TrySetResult(turn.Clip);

        listener.Start("mic");
        stream.Feed(0.5f, seconds: 1.0, block: 160);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var clip = await ended.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        clip.ShouldBeEmpty();
        listener.Stop();
    }

    [Fact]
    public async Task A_failed_microphone_is_reported_and_the_listener_stops_itself()
    {
        var stream = new FakeStream();
        using var listener = new OpenMicListener(stream, new ListeningModelStore(Path.GetTempPath(), new HttpClient()),
            () => new LevelVad(), () => new AlwaysComplete(), NullLogger<OpenMicListener>.Instance);
        var failed = new TaskCompletionSource<MicrophoneException>();
        listener.Failed += (_, run) => failed.TrySetResult(run.Failure!);

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
    public async Task Nothing_is_raised_after_a_stop_even_with_blocks_still_queued()
    {
        var stream = new FakeStream();
        using var listener = new OpenMicListener(stream, new ListeningModelStore(Path.GetTempPath(), new HttpClient()),
            () => new LevelVad(), () => new AlwaysComplete(), NullLogger<OpenMicListener>.Instance);
        var heard = 0;
        listener.Heard += (_, _) =>
        {
            if (Interlocked.Increment(ref heard) == 1)
            {
                listener.Stop(); // from the worker's own handler: must not wait on itself
            }
        };

        listener.Start("mic");
        stream.Feed(0f, seconds: 1.0, block: 160);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        heard.ShouldBe(1);
        stream.Running.ShouldBeFalse();
    }

    [Fact]
    public async Task A_failure_stop_does_not_close_a_run_started_in_the_meantime()
    {
        var stream = new FakeStream();
        using var listener = new OpenMicListener(stream, new ListeningModelStore(Path.GetTempPath(), new HttpClient()),
            () => new LevelVad(), () => new AlwaysComplete(), NullLogger<OpenMicListener>.Instance);
        listener.Failed += (_, _) => listener.Start("mic"); // the consumer restarts at once

        listener.Start("mic");
        stream.Fail();
        await Task.Delay(300, TestContext.Current.CancellationToken);

        stream.Running.ShouldBeTrue();
        listener.Stop();
    }

    [Fact]
    public void A_turn_end_model_that_will_not_load_disposes_the_voice_activity_model_that_did()
    {
        var vad = new LevelVad();
        var folder = Path.Combine(Path.GetTempPath(), "csx-om-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var store = new ListeningModelStore(folder, new HttpClient());
        var path = store.PathOf(ListeningModelStore.SmartTurn);
        File.WriteAllBytes(path, [1, 2, 3]); // ONNX Runtime refuses it
        using var listener = new OpenMicListener(new FakeStream(), store, () => vad, () => new SmartTurn(path),
            NullLogger<OpenMicListener>.Instance);

        var error = Should.Throw<ListeningModelException>(() => listener.Start("mic"));

        error.Model.ShouldBe(ListeningModelStore.SmartTurn);
        vad.Disposed.ShouldBeTrue();
        File.Exists(path).ShouldBeFalse();
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void A_load_failure_that_is_not_the_file_s_fault_keeps_the_file_and_is_thrown_as_it_is()
    {
        var vad = new LevelVad();
        var folder = Path.Combine(Path.GetTempPath(), "csx-om-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var store = new ListeningModelStore(folder, new HttpClient());
        var path = store.PathOf(ListeningModelStore.SmartTurn);
        File.WriteAllBytes(path, [1, 2, 3]);
        using var listener = new OpenMicListener(new FakeStream(), store, () => vad,
            () => throw new DllNotFoundException("onnxruntime.dll"), NullLogger<OpenMicListener>.Instance);

        Should.Throw<DllNotFoundException>(() => listener.Start("mic"));

        File.Exists(path).ShouldBeTrue("a good model must not be deleted for a failure that is not the file's");
        vad.Disposed.ShouldBeTrue();
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task Audio_dropped_because_the_worker_fell_behind_is_logged_once()
    {
        var stream = new FakeStream();
        var logger = new ListLogger<OpenMicListener>();
        using var listener = new OpenMicListener(stream, new ListeningModelStore(Path.GetTempPath(), new HttpClient()),
            () => new LevelVad(), () => new AlwaysComplete(), logger);
        using var stalled = new ManualResetEventSlim();
        listener.Heard += (_, _) => stalled.Wait(TimeSpan.FromSeconds(5)); // the worker stalls on its first batch

        listener.Start("mic");
        stream.Feed(0.01f, seconds: 30.0, block: 160); // 3000 blocks: three times what the queue holds
        stalled.Set();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        listener.Stop();

        logger.Entries.Count(e => e.Message.Contains("fell behind")).ShouldBe(1);
    }

    [Fact]
    public void Blocks_that_arrive_after_a_stop_are_not_taken_for_dropped_audio()
    {
        var stream = new FakeStream();
        var logger = new ListLogger<OpenMicListener>();
        using var listener = new OpenMicListener(stream, new ListeningModelStore(Path.GetTempPath(), new HttpClient()),
            () => new LevelVad(), () => new AlwaysComplete(), logger);

        var run = listener.Start("mic");
        listener.Stop(run);
        stream.Feed(0.01f, seconds: 0.1, block: 160); // late blocks from the capture thread

        logger.Entries.ShouldNotContain(e => e.Message.Contains("fell behind"));
    }

    [Fact]
    public void A_stop_of_an_old_run_leaves_the_newer_run_open()
    {
        var stream = new FakeStream();
        using var listener = new OpenMicListener(stream, new ListeningModelStore(Path.GetTempPath(), new HttpClient()),
            () => new LevelVad(), () => new AlwaysComplete(), NullLogger<OpenMicListener>.Instance);

        var old = listener.Start("mic");
        var current = listener.Start("mic");
        listener.Stop(old);

        stream.Running.ShouldBeTrue();
        listener.Stop(current);
        stream.Running.ShouldBeFalse();
    }

    [Fact]
    public async Task A_stop_from_the_worker_s_own_handler_disposes_the_run_s_token_once_the_worker_ends()
    {
        var stream = new FakeStream();
        using var listener = new OpenMicListener(stream, new ListeningModelStore(Path.GetTempPath(), new HttpClient()),
            () => new LevelVad(), () => new AlwaysComplete(), NullLogger<OpenMicListener>.Instance);
        OpenMicRun? run = null;
        var stopped = new TaskCompletionSource();
        listener.Heard += (_, _) =>
        {
            listener.Stop(run!);
            stopped.TrySetResult();
        };

        run = listener.Start("mic");
        var token = CurrentToken(listener);
        stream.Feed(0.01f, seconds: 0.1, block: 160);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        for (var i = 0; i < 100 && !Disposed(token); i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Disposed(token).ShouldBeTrue();
    }

    private static CancellationTokenSource CurrentToken(OpenMicListener listener) =>
        (CancellationTokenSource)typeof(OpenMicListener).GetField("_cts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(listener)!;

    /// <summary>A disposed source throws when its token is asked for.</summary>
    private static bool Disposed(CancellationTokenSource source)
    {
        try
        {
            _ = source.Token;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    // Needs the models in %LOCALAPPDATA%\CodeSwitchX\models\listening.
    [Fact(Explicit = true)]
    public async Task A_sentence_with_a_one_second_pause_in_the_middle_is_one_turn_and_ends_promptly()
    {
        var store = new ListeningModelStore(Path.Combine(CodeSwitchX.Core.AppPaths.Default().ModelsDirectory, "listening"), new HttpClient());
        var path = Path.Combine(Path.GetTempPath(), "csx-open-mic-pause.pcm");
        var audio = Speak("I would like to open the").Concat(new float[16_000]).Concat(Speak("Diffusion Nexus workspace please.")).Concat(new float[3 * 16_000]).ToArray();
        // No comma: SAPI pauses 0.45 s at one, Smart Turn may call the turn complete there, and the short "please" after
        // it is then dropped as a burst; this test is about the pause in the middle and the end.
        var endOfSpeech = Array.FindLastIndex(audio, s => Math.Abs(s) > 0.01f) + 1;
        WritePcm(path, audio);
        // In real time: a file fed faster than the worker reads it would have blocks dropped from the queue.
        using var listener = new OpenMicListener(new FileMicrophoneStream(path, realTime: true), store, NullLogger<OpenMicListener>.Instance);
        var turns = new List<(float[] Clip, long At)>();
        long heard = 0; // samples the worker has taken in: Heard and TurnEnded are both raised on it, in order
        listener.Heard += (_, batch) => heard += batch.Duration.Ticks / 625; // 625 ticks a sample at 16 kHz
        listener.TurnEnded += (_, turn) => { lock (turns) { turns.Add((turn.Clip, heard)); } };

        listener.Start("file");
        await Task.Delay(TimeSpan.FromSeconds(audio.Length / 16_000.0 + 1), TestContext.Current.CancellationToken);
        listener.Stop();

        lock (turns)
        {
            turns.Count.ShouldBe(1, "the pause mid-sentence must not end the turn");
            (turns[0].Clip.Length / 16_000.0).ShouldBeGreaterThan(3.0);
            var late = (turns[0].At - endOfSpeech) / 16_000.0;
            late.ShouldBeInRange(0.0, 1.0, $"the turn ended {late:0.00} s after the speech: Smart Turn must end it, not the {TurnDetector.GiveUp.TotalSeconds} s fallback");
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

        /// <summary>The next frame throws, once.</summary>
        public bool ThrowOnce
        {
            get => Volatile.Read(ref _throwOnce);
            set => Volatile.Write(ref _throwOnce, value);
        }

        private bool _throwOnce;

        public float Step(ReadOnlySpan<float> frame)
        {
            if (ThrowOnce)
            {
                ThrowOnce = false;
                throw new InvalidOperationException("model failed");
            }

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
