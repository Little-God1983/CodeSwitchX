namespace CodeSwitchX.Voice.Tests.Dictation;

using CodeSwitchX.Voice.Dictation;
using Microsoft.Extensions.Options;

// Ported from ContentAutomatorX.
public sealed class WhisperModelStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "csx-models-" + Guid.NewGuid().ToString("N"));

    private WhisperModelStore Store(WhisperModel model) =>
        new(Options.Create(new DictationOptions { ModelFolder = _folder, Model = model }));

    [Theory]
    [InlineData(WhisperModel.TinyEnglish, "ggml-tiny.en.bin")]
    [InlineData(WhisperModel.BaseEnglish, "ggml-base.en.bin")]
    [InlineData(WhisperModel.SmallEnglish, "ggml-small.en.bin")]
    [InlineData(WhisperModel.LargeV3Turbo, "ggml-large-v3-turbo.bin")]
    public void The_model_path_is_the_folder_plus_the_model_file(WhisperModel model, string file)
    {
        Store(model).ModelPath.ShouldBe(Path.Combine(_folder, file));
    }

    // A model added to the enum and forgotten in the store throws on the download. Walking the enum catches it here;
    // WhisperModelStore.Info is the single switch all three mappings come from.
    [Fact]
    public void Every_model_has_a_file_name_and_a_size()
    {
        foreach (var model in Enum.GetValues<WhisperModel>())
        {
            string.IsNullOrWhiteSpace(WhisperModelStore.FileName(model)).ShouldBeFalse($"no file name for {model}");
            WhisperModelStore.ApproximateBytes(model).ShouldBeGreaterThan(0, $"no size for {model}");
        }
    }

    [Fact]
    public void A_missing_folder_means_the_model_is_not_present()
    {
        var store = Store(WhisperModel.BaseEnglish);

        store.IsPresent.ShouldBeFalse();
        store.SizeBytes.ShouldBeNull();
    }

    [Fact]
    public void A_partial_download_is_not_a_present_model()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, "ggml-base.en.bin.partial"), new byte[10]);

        Store(WhisperModel.BaseEnglish).IsPresent.ShouldBeFalse();
    }

    [Fact]
    public void An_existing_file_is_present_and_reports_its_size()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, "ggml-base.en.bin"), new byte[123]);

        var store = Store(WhisperModel.BaseEnglish);

        store.IsPresent.ShouldBeTrue();
        store.SizeBytes.ShouldBe(123);
    }

    [Fact]
    public void The_approximate_size_grows_with_the_model()
    {
        WhisperModelStore.ApproximateBytes(WhisperModel.TinyEnglish)
            .ShouldBeLessThan(WhisperModelStore.ApproximateBytes(WhisperModel.BaseEnglish));
        WhisperModelStore.ApproximateBytes(WhisperModel.BaseEnglish)
            .ShouldBeLessThan(WhisperModelStore.ApproximateBytes(WhisperModel.SmallEnglish));
        WhisperModelStore.ApproximateBytes(WhisperModel.SmallEnglish)
            .ShouldBeLessThan(WhisperModelStore.ApproximateBytes(WhisperModel.LargeV3Turbo));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}
