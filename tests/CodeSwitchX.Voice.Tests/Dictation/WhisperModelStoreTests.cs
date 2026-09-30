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

    // A proxy or CDN closing a length-less response early ends the stream without an error. Installed, the cut file
    // would fail every press with a load error and never be downloaded again.
    [Fact]
    public async Task A_download_that_ends_early_is_not_installed_and_says_so()
    {
        var approximate = WhisperModelStore.ApproximateBytes(WhisperModel.TinyEnglish);
        var store = Downloading(WhisperModel.TinyEnglish, new NonSeekableStream(approximate / 2));

        var ex = await Should.ThrowAsync<IOException>(() => store.DownloadAsync(null, CancellationToken.None));

        ex.Message.ShouldStartWith("the download ended early, after ");
        ex.Message.ShouldContain($"of about {approximate:N0} bytes");
        store.IsPresent.ShouldBeFalse();
        File.Exists(store.ModelPath + ".partial").ShouldBeFalse();
    }

    [Fact]
    public async Task A_length_less_download_within_a_percent_of_the_model_size_is_installed()
    {
        var approximate = WhisperModelStore.ApproximateBytes(WhisperModel.TinyEnglish);
        var store = Downloading(WhisperModel.TinyEnglish, new NonSeekableStream((long)(approximate * 0.995)));
        var reports = new List<double>();

        await store.DownloadAsync(new SyncProgress(reports.Add), CancellationToken.None);

        store.IsPresent.ShouldBeTrue();
        store.SizeBytes.ShouldBe((long)(approximate * 0.995));
        reports[^1].ShouldBe(1.0);
    }

    [Fact]
    public async Task A_download_of_known_length_must_deliver_all_of_it()
    {
        var store = Downloading(WhisperModel.TinyEnglish, new ShortSeekableStream(length: 1000, delivers: 999));

        var ex = await Should.ThrowAsync<IOException>(() => store.DownloadAsync(null, CancellationToken.None));

        ex.Message.ShouldContain($"of {1000:N0} bytes");
        store.IsPresent.ShouldBeFalse();
        File.Exists(store.ModelPath + ".partial").ShouldBeFalse();
    }

    [Fact]
    public async Task A_download_of_known_length_is_installed_when_whole_however_small()
    {
        var store = Downloading(WhisperModel.TinyEnglish, new ShortSeekableStream(length: 1000, delivers: 1000));

        await store.DownloadAsync(null, CancellationToken.None);

        store.SizeBytes.ShouldBe(1000);
    }

    private WhisperModelStore Downloading(WhisperModel model, Stream source) =>
        new(Options.Create(new DictationOptions { ModelFolder = _folder, Model = model }))
        {
            OpenDownload = (_, _) => Task.FromResult(source),
        };

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>A length-less HTTP body: zeros, then the end, as a cut connection looks.</summary>
    private sealed class NonSeekableStream(long bytes) : Stream
    {
        private long _left = bytes;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, _left);
            Array.Clear(buffer, offset, n);
            _left -= n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Says how long it is and ends before that.</summary>
    private sealed class ShortSeekableStream(long length, long delivers) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, delivers - _position);
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
