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
        public WhisperModel Model => WhisperModel.BaseEnglish;
        public string ModelPath => @"C:\nowhere\ggml-base.en.bin";
        public bool IsPresent => false;
        public long? SizeBytes => null;
        public string? LoadedRuntime => null;
        public Task DownloadAsync(IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class PresentStore(string path) : IWhisperModelStore
    {
        public WhisperModel Model => WhisperModel.BaseEnglish;
        public string ModelPath => path;
        public bool IsPresent => true;
        public long? SizeBytes => new FileInfo(path).Length;
        public string? LoadedRuntime => "Vulkan";
        public Task DownloadAsync(IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
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

        var result = await Service().TranscribeAsync(samples, DictationVocabulary.Empty, live: false, TestContext.Current.CancellationToken);

        result.Text.ShouldBe("");
        result.AudioLength.ShouldBe(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task A_long_enough_clip_without_the_model_throws_model_missing()
    {
        var samples = new float[AudioMath.TargetRate]; // 1 s

        var ex = await Should.ThrowAsync<DictationModelMissingException>(() =>
            Service().TranscribeAsync(samples, DictationVocabulary.Empty, live: false, TestContext.Current.CancellationToken));

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
                service.TranscribeAsync(new float[AudioMath.TargetRate], DictationVocabulary.Empty, live: false, TestContext.Current.CancellationToken));
        }
        finally
        {
            Cleanup(folder);
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
                service.TranscribeAsync(new float[AudioMath.TargetRate], DictationVocabulary.Empty, live: false, TestContext.Current.CancellationToken));

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
}
