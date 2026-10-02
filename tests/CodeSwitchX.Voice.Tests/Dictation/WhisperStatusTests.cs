namespace CodeSwitchX.Voice.Tests.Dictation;

using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Dictation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>What the dots of the speech-to-text model show, and the download the Raven panel and Settings share.</summary>
public sealed class WhisperStatusTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "csx-whisper-status-" + Guid.NewGuid().ToString("N"));
    private readonly GatedStream _download = new(1000);
    private readonly WhisperModelStore _store;
    private readonly WhisperDictationService _service;
    private readonly List<DictationStatus> _told = [];
    private int _downloads;

    public WhisperStatusTests()
    {
        var options = Options.Create(new DictationOptions { ModelFolder = _folder, Model = WhisperModel.BaseEnglish });
        _store = new WhisperModelStore(options) { OpenDownload = (_, _) => { Interlocked.Increment(ref _downloads); return Task.FromResult<Stream>(_download); } };
        _service = new WhisperDictationService(_store, options, NullLogger<WhisperDictationService>.Instance);
        _service.StatusChanged += (_, status) => { lock (_told) { _told.Add(status); } };
    }

    public void Dispose()
    {
        _service.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private List<DictationStatus> Told
    {
        get
        {
            lock (_told)
            {
                return [.. _told];
            }
        }
    }

    [Fact]
    public void A_model_not_on_disk_is_not_downloaded_and_one_on_disk_is_asleep()
    {
        _service.Status.ShouldBe(new DictationStatus(DictationState.NotDownloaded, WhisperModel.BaseEnglish));

        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(_store.ModelPath, new byte[10]);

        _service.Status.ShouldBe(new DictationStatus(DictationState.Asleep, WhisperModel.BaseEnglish));
    }

    [Fact]
    public async Task A_download_is_told_while_it_runs_and_the_panel_and_Settings_share_it()
    {
        var fromPanel = _store.DownloadAsync(null, CancellationToken.None);
        var fromSettings = _store.DownloadAsync(null, CancellationToken.None);
        await Until(() => _service.Status.State == DictationState.Downloading);
        _service.Status.Bytes.ShouldBe(new ByteProgress(0, WhisperModelStore.ApproximateBytes(WhisperModel.BaseEnglish)));

        _download.Open();
        await Task.WhenAll(fromPanel, fromSettings);

        _downloads.ShouldBe(1, "one download for both");
        await Until(() => Told.LastOrDefault()?.State == DictationState.Asleep);
        Told.Select(s => s.State).Distinct().ShouldBe([DictationState.Downloading, DictationState.Asleep], "told as it goes, then on disk");
    }

    [Fact]
    public async Task Waiting_for_a_download_can_be_given_up_without_stopping_it()
    {
        using var stop = new CancellationTokenSource();
        var waiting = _store.DownloadAsync(null, stop.Token);

        await stop.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => waiting);
        _download.Open();

        await Until(() => _store.IsPresent);
    }

    [Fact]
    public async Task A_model_that_will_not_load_is_failed_with_why_and_another_model_is_told_at_once()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllBytesAsync(_store.ModelPath, new byte[4096], TestContext.Current.CancellationToken); // fails on the magic number

        await Should.ThrowAsync<DictationModelLoadException>(() =>
            _service.TranscribeAsync(new float[AudioMath.TargetRate], DictationVocabulary.Empty, TestContext.Current.CancellationToken));

        _service.Status.State.ShouldBe(DictationState.Failed);
        _service.Status.Detail.ShouldNotBeNull().ShouldContain("could not load ggml-base.en.bin");
        Told.Select(s => s.State).ShouldBe([DictationState.Loading, DictationState.Failed]);

        _store.Model = WhisperModel.TinyEnglish;

        await Until(() => Told.LastOrDefault() == new DictationStatus(DictationState.NotDownloaded, WhisperModel.TinyEnglish));
    }

    [Fact]
    public void Each_model_says_whether_it_is_on_disk_whichever_is_used()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, WhisperModelStore.FileName(WhisperModel.SmallEnglish)), new byte[10]);

        _store.IsPresentFor(WhisperModel.SmallEnglish).ShouldBeTrue();
        _store.IsPresentFor(WhisperModel.BaseEnglish).ShouldBeFalse();
        _store.IsPresent.ShouldBeFalse();
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline);
            await Task.Delay(5);
        }
    }

    /// <summary>A download of known length that gives nothing until opened.</summary>
    private sealed class GatedStream(long length) : Stream
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _position;

        public void Open() => _open.TrySetResult();

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await _open.Task;
            var n = (int)Math.Min(buffer.Length, length - _position);
            buffer.Span[..n].Clear();
            _position += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
