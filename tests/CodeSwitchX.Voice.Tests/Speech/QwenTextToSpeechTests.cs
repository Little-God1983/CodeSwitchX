namespace CodeSwitchX.Voice.Tests.Speech;

using System.Net;
using System.Text.Json;
using CodeSwitchX.Voice.Speech;
using CodeSwitchX.Voice.Speech.QwenTts;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class QwenTextToSpeechTests : IDisposable
{
    private readonly FakeEnvironment _environment = new();
    private readonly FakeLauncher _launcher = new();
    private readonly SpeechSettings _settings = new();
    private readonly FakeHandler _handler = new();
    private readonly QwenTextToSpeech _tts;
    private readonly List<TextToSpeechState> _states = [];

    public QwenTextToSpeechTests()
    {
        _tts = new QwenTextToSpeech(_environment, _launcher, _settings, new HttpClient(_handler), NullLogger<QwenTextToSpeech>.Instance);
        _tts.StatusChanged += (_, status) => { lock (_states) { _states.Add(status.State); } };
    }

    public void Dispose() => _tts.Dispose();

    private List<TextToSpeechState> States
    {
        get
        {
            lock (_states)
            {
                return [.. _states];
            }
        }
    }

    [Fact]
    public async Task Without_an_install_a_warm_up_does_nothing()
    {
        _tts.Prepare(install: false);
        await _tts.Preparing;
        _tts.Status.State.ShouldBe(TextToSpeechState.Off);
        _environment.Installs.ShouldBe(0);
        _launcher.Starts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_first_need_installs_then_loads_the_model_of_the_settings()
    {
        _settings.Model = SpeechModel.Large;
        _tts.Prepare(install: true);
        await _tts.Preparing;

        _environment.Installs.ShouldBe(1);
        _launcher.Starts.ShouldBe(["Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice"]);
        States.ShouldBe([TextToSpeechState.Installing, TextToSpeechState.Loading, TextToSpeechState.Ready]);
    }

    [Fact]
    public async Task An_installed_voice_starts_without_installing()
    {
        _environment.Installed = true;
        _tts.Prepare(install: false);
        await _tts.Preparing;
        _environment.Installs.ShouldBe(0);
        _tts.Status.State.ShouldBe(TextToSpeechState.Ready);
    }

    [Fact]
    public async Task A_failure_is_not_tried_again_until_the_model_changes()
    {
        _environment.InstallFails = true;
        _tts.Prepare(install: true);
        await _tts.Preparing;
        _tts.Status.ShouldBe(new TextToSpeechStatus(TextToSpeechState.Failed, "no network"));

        _tts.Prepare(install: true);
        await _tts.Preparing;
        _environment.Installs.ShouldBe(1);

        _environment.InstallFails = false;
        _settings.Model = SpeechModel.Large;
        _tts.Prepare(install: true);
        await _tts.Preparing;
        _environment.Installs.ShouldBe(2);
        _tts.Status.State.ShouldBe(TextToSpeechState.Ready);
    }

    [Fact]
    public async Task Speaking_before_it_is_ready_says_so_and_gets_it_ready()
    {
        var error = await Should.ThrowAsync<TextToSpeechNotReadyException>(async () =>
        {
            await foreach (var _ in _tts.SpeakAsync("Hello.", CancellationToken.None))
            {
            }
        });

        await _tts.Preparing;
        _environment.Installs.ShouldBe(1);
        _tts.Status.State.ShouldBe(TextToSpeechState.Ready);
        error.Status.State.ShouldNotBe(TextToSpeechState.Ready);
    }

    [Fact]
    public async Task Speech_streams_as_whole_samples_with_the_voice_of_the_settings()
    {
        _environment.Installed = true;
        _settings.Voice = "aiden";
        _tts.Prepare(install: false);
        await _tts.Preparing;
        _handler.Body = [1, 2, 3, 4, 5, 6, 7];
        _handler.ReadSize = 3;

        var chunks = new List<byte[]>();
        await foreach (var chunk in _tts.SpeakAsync("Hello there.", CancellationToken.None))
        {
            chunk.Pcm16.Length.ShouldBe(chunk.Pcm16.Length / 2 * 2);
            chunk.SampleRate.ShouldBe(24000);
            chunks.Add(chunk.Pcm16.ToArray());
        }

        chunks.SelectMany(c => c).ShouldBe(new byte[] { 1, 2, 3, 4, 5, 6 }); // the odd last byte is no sample
        _handler.Request!.RootElement.GetProperty("input").GetString().ShouldBe("Hello there.");
        _handler.Request.RootElement.GetProperty("voice").GetString().ShouldBe("aiden");
        _handler.Authorization.ShouldBe("Bearer token");
        _handler.ContentLength.ShouldNotBeNull("the sidecar reads no chunked request bodies");
    }

    [Fact]
    public async Task A_new_model_restarts_the_voice()
    {
        _environment.Installed = true;
        _tts.Prepare(install: false);
        await _tts.Preparing;
        var first = _launcher.Servers.Single();

        _settings.Model = SpeechModel.Large;
        await _tts.Preparing;

        first.Disposed.ShouldBeTrue();
        _launcher.Starts.ShouldBe(["Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice", "Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice"]);
        _tts.Status.State.ShouldBe(TextToSpeechState.Ready);
    }

    [Fact]
    public async Task A_sidecar_that_ends_by_itself_is_started_again_when_next_needed()
    {
        _environment.Installed = true;
        _tts.Prepare(install: false);
        await _tts.Preparing;

        _launcher.Servers.Single().End();
        await Until(() => _tts.Status.State == TextToSpeechState.Off);
        _tts.Prepare(install: false);
        await _tts.Preparing;
        _launcher.Starts.Count.ShouldBe(2);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline);
            await Task.Delay(5);
        }
    }

    private sealed class FakeEnvironment : IQwenTtsEnvironment
    {
        public bool Installed { get; set; }

        public bool InstallFails { get; set; }

        public int Installs { get; private set; }

        public bool IsInstalled => Installed;

        public string Python => "python.exe";

        public string ModelCache => "cache";

        public string WriteScript() => "server.py";

        public Task InstallAsync(IProgress<string> progress, CancellationToken ct)
        {
            Installs++;
            if (InstallFails)
            {
                throw new TextToSpeechException("no network");
            }

            Installed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLauncher : IQwenTtsServerLauncher
    {
        public List<string> Starts { get; } = [];

        public List<FakeServer> Servers { get; } = [];

        public Task<IQwenTtsServer> StartAsync(IQwenTtsEnvironment environment, string modelId, Action<string> onStatus, CancellationToken ct)
        {
            Starts.Add(modelId);
            var server = new FakeServer();
            Servers.Add(server);
            return Task.FromResult<IQwenTtsServer>(server);
        }
    }

    private sealed class FakeServer : IQwenTtsServer
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Uri Address { get; } = new("http://127.0.0.1:5000/");

        public string Token => "token";

        public Task Exited => _exited.Task;

        public bool Disposed { get; private set; }

        public void End() => _exited.TrySetResult();

        public void Dispose()
        {
            Disposed = true;
            End();
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public byte[] Body { get; set; } = [];

        public int ReadSize { get; set; } = 4096;

        public JsonDocument? Request { get; private set; }

        public string? Authorization { get; private set; }

        public long? ContentLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ContentLength = request.Content!.Headers.ContentLength; // before the read below, which buffers it and so gives it one
            Request = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            Authorization = request.Headers.Authorization?.ToString();
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Trickle(Body, ReadSize)) };
            response.Headers.Add("X-Sample-Rate", "24000");
            return response;
        }
    }

    /// <summary>Gives at most a few bytes per read, as a network stream may.</summary>
    private sealed class Trickle(byte[] data, int size) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, size));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, size)], ct);
    }
}
