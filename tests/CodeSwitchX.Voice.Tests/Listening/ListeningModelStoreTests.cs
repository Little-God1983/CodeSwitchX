using System.Net;
using System.Security.Cryptography;
using CodeSwitchX.Voice.Listening;

namespace CodeSwitchX.Voice.Tests.Listening;

public sealed class ListeningModelStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "csx-listening-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task Both_models_are_downloaded_checked_and_put_in_place()
    {
        var a = new byte[] { 1, 2, 3 };
        var b = new byte[] { 4, 5, 6, 7 };
        var store = Store(new Files { ["a.onnx"] = a, ["b.onnx"] = b }, Model("a.onnx", a), Model("b.onnx", b));
        var reported = new List<double>();

        await store.DownloadAsync(new Progress(reported), TestContext.Current.CancellationToken);

        store.IsPresent.ShouldBeTrue();
        File.ReadAllBytes(Path.Combine(_folder, "a.onnx")).ShouldBe(a);
        reported[^1].ShouldBe(1.0);
        Directory.GetFiles(_folder, "*.partial").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_download_whose_hash_does_not_match_leaves_nothing_behind()
    {
        var good = new byte[] { 1, 2, 3 };
        var store = Store(new Files { ["a.onnx"] = [9, 9, 9] }, Model("a.onnx", good));

        var error = await Should.ThrowAsync<IOException>(() => store.DownloadAsync(null, TestContext.Current.CancellationToken));

        error.Message.ShouldContain("a.onnx");
        store.IsPresent.ShouldBeFalse();
        Directory.GetFiles(_folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_model_already_in_place_is_not_downloaded_again_and_a_forgotten_one_is()
    {
        var a = new byte[] { 1, 2, 3 };
        var files = new Files { ["a.onnx"] = a };
        var model = Model("a.onnx", a);
        var store = Store(files, model);
        await store.DownloadAsync(null, TestContext.Current.CancellationToken);

        await store.DownloadAsync(null, TestContext.Current.CancellationToken);
        files.Requests.ShouldBe(1);

        store.Forget(model);
        store.IsPresent.ShouldBeFalse();
        await store.DownloadAsync(null, TestContext.Current.CancellationToken);
        files.Requests.ShouldBe(2);
    }

    [Fact]
    public async Task A_download_that_stalls_fails_and_leaves_nothing_behind()
    {
        var a = new byte[] { 1, 2, 3 };
        var store = new ListeningModelStore(_folder, new HttpClient(new Stalls())) { Models = [Model("a.onnx", a)], StallTimeout = TimeSpan.FromMilliseconds(200) };

        var error = await Should.ThrowAsync<IOException>(() => store.DownloadAsync(null, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        error.Message.ShouldContain("stalled");
        Directory.GetFiles(_folder).ShouldBeEmpty();
    }

    [Fact]
    public void The_pinned_models_are_the_ones_the_spec_names()
    {
        ListeningModelStore.Silero.Length.ShouldBe(2_327_524);
        ListeningModelStore.Silero.Sha256.ShouldBe("1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3");
        ListeningModelStore.SmartTurn.Length.ShouldBe(8_679_182);
        ListeningModelStore.SmartTurn.Sha256.ShouldBe("2bb026316b14a660486a75b1733cd3fbab8c2fd0314dc9af7be49f8cca967e4f");
    }

    private ListeningModelStore Store(Files files, params ListeningModel[] models) =>
        new(_folder, new HttpClient(files)) { Models = models };

    private static ListeningModel Model(string name, byte[] content) =>
        new(name, new Uri("https://models.test/" + name), content.Length, Convert.ToHexStringLower(SHA256.HashData(content)));

    private sealed class Progress(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }

    /// <summary>Answers with headers, then a body that never sends a byte.</summary>
    private sealed class Stalls : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SilentStream()) });
    }

    private sealed class SilentStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }

    private sealed class Files : HttpMessageHandler, System.Collections.IEnumerable
    {
        private readonly Dictionary<string, byte[]> _files = [];

        public int Requests { get; private set; }

        public byte[] this[string name] { set => _files[name] = value; }

        public System.Collections.IEnumerator GetEnumerator() => _files.GetEnumerator();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(_files.TryGetValue(request.RequestUri!.Segments[^1], out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
