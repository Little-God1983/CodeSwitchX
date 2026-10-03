namespace CodeSwitchX.Voice.Tests.Dictation;

using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Dictation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// Ported from ContentAutomatorX.
public sealed class WhisperDictationServiceTests
{
    private sealed class MissingStore : IWhisperModelStore
    {
        public WhisperModel Model { get; set; } = WhisperModel.BaseEnglish;
        public string ModelPath => @"C:\nowhere\ggml-base.en.bin";
        public bool IsPresent => false;
        public string? LoadedRuntime => null;
        public ModelDownload? Download => null;
        public event EventHandler? ModelChanged { add { } remove { } }
        public event EventHandler? DownloadChanged { add { } remove { } }
        public Task DownloadAsync(IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task DownloadAsync(WhisperModel model, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<ModelDownload> Downloads => [];
        public bool IsPresentOf(WhisperModel model) => IsPresent;
    }

    private sealed class PresentStore(string path) : IWhisperModelStore
    {
        public WhisperModel Model { get; set; } = WhisperModel.BaseEnglish;
        public string ModelPath => path;
        public bool IsPresent => true;
        public string? LoadedRuntime => "Vulkan";
        public ModelDownload? Download => null;
        public event EventHandler? ModelChanged { add { } remove { } }
        public event EventHandler? DownloadChanged { add { } remove { } }
        public Task DownloadAsync(IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task DownloadAsync(WhisperModel model, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<ModelDownload> Downloads => [];
        public bool IsPresentOf(WhisperModel model) => IsPresent;
    }

    /// <summary>A damaged model whose path is read only once the test lets it: the load, and with it the gate, is held
    /// for as long as the test wants, the way a long inference holds it.</summary>
    private sealed class BlockingStore(string path) : IWhisperModelStore, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Proceed { get; } = new();
        public int LoadThread { get; private set; } = -1;

        public WhisperModel Model { get; set; } = WhisperModel.BaseEnglish;

        public string ModelPath
        {
            get
            {
                if (!Entered.IsSet)
                {
                    LoadThread = Environment.CurrentManagedThreadId;
                    Entered.Set();
                    Proceed.Wait(TimeSpan.FromSeconds(10));
                }

                return path;
            }
        }

        public bool IsPresent => true;
        public string? LoadedRuntime => null;
        public ModelDownload? Download => null;
        public event EventHandler? ModelChanged { add { } remove { } }
        public event EventHandler? DownloadChanged { add { } remove { } }
        public Task DownloadAsync(IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task DownloadAsync(WhisperModel model, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<ModelDownload> Downloads => [];
        public bool IsPresentOf(WhisperModel model) => IsPresent;

        public void Dispose()
        {
            Entered.Dispose();
            Proceed.Dispose();
        }
    }

    private static WhisperDictationService Service(TimeSpan? minimum = null) =>
        new(new MissingStore(),
            Options.Create(new DictationOptions { ModelFolder = @"C:\nowhere", MinimumClip = minimum ?? TimeSpan.FromMilliseconds(500) }),
            NullLogger<WhisperDictationService>.Instance);

    // A zero-filled file stands in for a damaged model: whisper.cpp rejects it on the magic number, the same path a
    // truncated download takes.
    private static async Task<string> BrokenModelFolder(string prefix)
    {
        var folder = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "ggml-base.en.bin"), new byte[4096]);
        return folder;
    }

    private static void Cleanup(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public async Task A_short_clip_returns_empty_text_without_needing_the_model()
    {
        var samples = new float[AudioMath.TargetRate / 4]; // 250 ms of silence

        var result = await Service().TranscribeAsync(samples, DictationVocabulary.Empty, TestContext.Current.CancellationToken);

        result.Text.ShouldBe("");
        result.AudioLength.ShouldBe(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task A_long_enough_clip_without_the_model_throws_model_missing()
    {
        var samples = new float[AudioMath.TargetRate]; // 1 s

        var ex = await Should.ThrowAsync<DictationModelMissingException>(() =>
            Service().TranscribeAsync(samples, DictationVocabulary.Empty, TestContext.Current.CancellationToken));

        ex.ModelPath.ShouldBe(@"C:\nowhere\ggml-base.en.bin");
    }

    // A model that has not been downloaded yet is the normal first-run case, so warming up must be a quiet no-op.
    [Fact]
    public async Task Warming_up_with_no_model_does_nothing_and_does_not_throw()
    {
        using var service = Service();

        await service.WarmUpAsync(TestContext.Current.CancellationToken);
    }

    // A damaged model download must not break the caller: warm-up runs before anyone waits on it. Transcribing still
    // reports the failure to whoever actually dictates.
    [Fact]
    public async Task Warming_up_with_a_broken_model_swallows_the_failure()
    {
        var folder = await BrokenModelFolder("csx-warm-");
        try
        {
            using var service = new WhisperDictationService(new PresentStore(Path.Combine(folder, "ggml-base.en.bin")),
                Options.Create(new DictationOptions { ModelFolder = folder }),
                NullLogger<WhisperDictationService>.Instance);

            await service.WarmUpAsync(TestContext.Current.CancellationToken);

            await Should.ThrowAsync<DictationModelLoadException>(() =>
                service.TranscribeAsync(new float[AudioMath.TargetRate], DictationVocabulary.Empty, TestContext.Current.CancellationToken));
        }
        finally
        {
            Cleanup(folder);
        }
    }

    // A model that failed to load is not loaded again by a warm-up while the file is unchanged: before, every press warmed
    // up (one load, failing) and then transcribed (a second load, failing the same way), so the error took twice as long.
    [Fact]
    public async Task A_warm_up_after_a_failed_load_returns_at_once_so_a_broken_model_loads_once_per_press()
    {
        var folder = await BrokenModelFolder("csx-warm-once-");
        try
        {
            var logger = new CountingLogger();
            using var service = new WhisperDictationService(new PresentStore(Path.Combine(folder, "ggml-base.en.bin")),
                Options.Create(new DictationOptions { ModelFolder = folder }), logger);

            await service.WarmUpAsync(TestContext.Current.CancellationToken);
            logger.Errors.ShouldBe(1, "the startup warm-up tries the model once");

            for (var press = 1; press <= 2; press++)
            {
                await service.WarmUpAsync(TestContext.Current.CancellationToken);
                await Should.ThrowAsync<DictationModelLoadException>(() =>
                    service.TranscribeAsync(new float[AudioMath.TargetRate], DictationVocabulary.Empty, TestContext.Current.CancellationToken));
                logger.Errors.ShouldBe(1 + press, "one load per press, by the transcription the user waits on");
            }
        }
        finally
        {
            Cleanup(folder);
        }
    }

    // The user replaces the damaged file (a new download, or a copy): the warm-up tries the new one.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_replaced_model_file_is_tried_again_by_the_warm_up(bool newLength)
    {
        var folder = await BrokenModelFolder("csx-warm-replaced-");
        var path = Path.Combine(folder, "ggml-base.en.bin");
        try
        {
            var logger = new CountingLogger();
            using var service = new WhisperDictationService(new PresentStore(path),
                Options.Create(new DictationOptions { ModelFolder = folder }), logger);
            await service.WarmUpAsync(TestContext.Current.CancellationToken);
            await service.WarmUpAsync(TestContext.Current.CancellationToken);
            logger.Errors.ShouldBe(1);

            if (newLength)
            {
                await File.WriteAllBytesAsync(path, new byte[8192], TestContext.Current.CancellationToken);
            }

            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
            await service.WarmUpAsync(TestContext.Current.CancellationToken);

            logger.Errors.ShouldBe(2);
        }
        finally
        {
            Cleanup(folder);
        }
    }

    /// <summary>Counts the errors logged; each failed load of the model logs exactly one.</summary>
    private sealed class CountingLogger : Microsoft.Extensions.Logging.ILogger<WhisperDictationService>
    {
        private int _errors;

        public int Errors => Volatile.Read(ref _errors);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Error)
            {
                Interlocked.Increment(ref _errors);
            }
        }
    }

    // A present but unloadable model is a different problem from a missing one. The message has to name what the user
    // can try, and the backend it failed on.
    [Fact]
    public async Task A_present_but_unloadable_model_says_what_to_try_and_on_which_backend()
    {
        var folder = await BrokenModelFolder("csx-badmodel-");
        var path = Path.Combine(folder, "ggml-base.en.bin");
        try
        {
            using var service = new WhisperDictationService(new PresentStore(path),
                Options.Create(new DictationOptions { ModelFolder = folder }),
                NullLogger<WhisperDictationService>.Instance);

            var ex = await Should.ThrowAsync<DictationModelLoadException>(() =>
                service.TranscribeAsync(new float[AudioMath.TargetRate], DictationVocabulary.Empty, TestContext.Current.CancellationToken));

            ex.ModelPath.ShouldBe(path);
            ex.Message.ShouldContain("ggml-base.en.bin");
            ex.Message.ShouldContain("Vulkan");
            ex.Message.ShouldContain("smaller model");
            ex.InnerException.ShouldNotBeNull();
        }
        finally
        {
            Cleanup(folder);
        }
    }

    // The caller is the UI thread. Loading a 1.6 GB model on it froze the window for seconds.
    [Fact]
    public async Task The_model_is_loaded_off_the_callers_thread_and_the_call_returns_at_once()
    {
        var folder = await BrokenModelFolder("csx-offthread-");
        using var store = new BlockingStore(Path.Combine(folder, "ggml-base.en.bin"));
        try
        {
            using var service = new WhisperDictationService(store,
                Options.Create(new DictationOptions { ModelFolder = folder }), NullLogger<WhisperDictationService>.Instance);

            var transcription = service.TranscribeAsync(new float[AudioMath.TargetRate], DictationVocabulary.Empty,
                TestContext.Current.CancellationToken);

            store.Entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ShouldBeTrue("the load started");
            transcription.IsCompleted.ShouldBeFalse();
            store.LoadThread.ShouldNotBe(Environment.CurrentManagedThreadId);
            store.Proceed.Set();
            await Should.ThrowAsync<DictationModelLoadException>(() => transcription);
        }
        finally
        {
            store.Proceed.Set();
            Cleanup(folder);
        }
    }

    // The app exits right after a release often enough: freeing the model under the inference that is still running
    // is a native use-after-free.
    [Fact]
    public async Task Dispose_waits_for_a_running_transcription_before_freeing_the_model()
    {
        var folder = await BrokenModelFolder("csx-dispose-");
        using var store = new BlockingStore(Path.Combine(folder, "ggml-base.en.bin"));
        try
        {
            var service = new WhisperDictationService(store,
                Options.Create(new DictationOptions { ModelFolder = folder }), NullLogger<WhisperDictationService>.Instance);
            var transcription = service.TranscribeAsync(new float[AudioMath.TargetRate], DictationVocabulary.Empty,
                TestContext.Current.CancellationToken);
            store.Entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ShouldBeTrue("the load started");

            var dispose = Task.Run(service.Dispose, TestContext.Current.CancellationToken);

            (await Task.WhenAny(dispose, Task.Delay(300, TestContext.Current.CancellationToken))).ShouldNotBe(dispose,
                "Dispose returned while the transcription still held the model");
            store.Proceed.Set();
            await Should.ThrowAsync<DictationModelLoadException>(() => transcription);
            await dispose.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            store.Proceed.Set();
            Cleanup(folder);
        }
    }

    // A stuck GPU must not hang the app's exit.
    [Fact]
    public async Task Dispose_gives_up_waiting_after_its_timeout()
    {
        var folder = await BrokenModelFolder("csx-dispose-timeout-");
        using var store = new BlockingStore(Path.Combine(folder, "ggml-base.en.bin"));
        try
        {
            var service = new WhisperDictationService(store,
                Options.Create(new DictationOptions { ModelFolder = folder }), NullLogger<WhisperDictationService>.Instance)
            {
                DisposeTimeout = TimeSpan.FromMilliseconds(100),
            };
            var transcription = service.TranscribeAsync(new float[AudioMath.TargetRate], DictationVocabulary.Empty,
                TestContext.Current.CancellationToken);
            store.Entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ShouldBeTrue("the load started");

            await Task.Run(service.Dispose, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            transcription.IsCompleted.ShouldBeFalse("the transcription is still running");
            store.Proceed.Set();
            await Should.ThrowAsync<DictationModelLoadException>(() => transcription);
        }
        finally
        {
            store.Proceed.Set();
            Cleanup(folder);
        }
    }

    [Fact]
    public async Task A_transcription_after_dispose_is_refused()
    {
        var folder = await BrokenModelFolder("csx-disposed-");
        try
        {
            var service = new WhisperDictationService(new PresentStore(Path.Combine(folder, "ggml-base.en.bin")),
                Options.Create(new DictationOptions { ModelFolder = folder }), NullLogger<WhisperDictationService>.Instance);
            service.Dispose();

            await Should.ThrowAsync<ObjectDisposedException>(() =>
                service.TranscribeAsync(new float[AudioMath.TargetRate], DictationVocabulary.Empty, TestContext.Current.CancellationToken));
        }
        finally
        {
            Cleanup(folder);
        }
    }

    // The warm-up decodes about a second of real speech: on silence Whisper keeps retrying its decoding for seconds.
    [Fact]
    public void The_warm_up_sample_is_about_a_second_of_speech_loud_enough_for_the_speech_gate()
    {
        var samples = WarmUpSpeech.Load();

        var seconds = (double)samples.Length / AudioMath.TargetRate;
        seconds.ShouldBeInRange(0.6, 2.0);
        var gate = new SpeechGate();
        for (var i = 0; i + 160 <= samples.Length; i += 160)
        {
            gate.Step(AudioMath.Rms(samples.AsSpan(i, 160)), TimeSpan.FromMilliseconds(10));
        }

        gate.Read().HeardSpeech.ShouldBeTrue();
    }
}
