namespace CodeSwitchX.Voice.Tests.Speech;

using System.Net;
using System.Text.Json;
using CodeSwitchX.Voice.Speech;
using CodeSwitchX.Voice.Speech.Sidecar;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

public sealed class SidecarTextToSpeechTests : IDisposable
{
    private readonly FakeEnvironment _environment = new();
    private readonly FakeLauncher _launcher = new();
    private readonly SpeechSettings _settings = new();
    private readonly FakeHandler _handler = new();
    private readonly FakeTimeProvider _time = new();
    private readonly SidecarTextToSpeech _tts;
    private readonly List<TextToSpeechState> _states = [];

    public SidecarTextToSpeechTests()
    {
        _tts = new SidecarTextToSpeech(SpeechEngine.Qwen, _environment, _launcher, _settings, new HttpClient(_handler), _time, NullLogger<SidecarTextToSpeech>.Instance);
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
    public async Task Without_an_install_a_warm_up_only_says_it_is_not_installed()
    {
        _tts.Prepare(install: false);
        await _tts.Preparing;
        _tts.Status.State.ShouldBe(TextToSpeechState.NotInstalled);
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
    public async Task A_failure_is_tried_again_once_a_while_has_passed()
    {
        _environment.InstallFails = true;
        _tts.Prepare(install: true);
        await _tts.Preparing;
        _environment.InstallFails = false;

        _time.Advance(SidecarTextToSpeech.RetryAfter - TimeSpan.FromSeconds(1));
        _tts.Prepare(install: true);
        await _tts.Preparing;
        _environment.Installs.ShouldBe(1);

        _time.Advance(TimeSpan.FromSeconds(1));
        _tts.Prepare(install: true);
        await _tts.Preparing;
        _environment.Installs.ShouldBe(2);
        _tts.Status.State.ShouldBe(TextToSpeechState.Ready);
    }

    [Fact]
    public async Task A_load_that_never_finishes_fails_after_the_timeout()
    {
        _environment.Installed = true;
        _launcher.Gate = new TaskCompletionSource(); // a download that stalls, a graph capture that never ends
        _launcher.HonorsCancel = true;
        _tts.Prepare(install: false);
        await Until(() => _launcher.Starts.Count == 1);

        _time.Advance(SidecarTextToSpeech.LoadTimeout);
        await _tts.Preparing;

        _tts.Status.ShouldBe(new TextToSpeechStatus(TextToSpeechState.Failed, "The voice did not finish loading in 20 minutes."));
    }

    [Fact]
    public async Task A_hung_voice_is_started_again()
    {
        _environment.Installed = true;
        _tts.Prepare(install: false);
        await _tts.Preparing;
        var hung = _launcher.Servers.Single();

        _tts.Recover();
        await Until(() => _launcher.Starts.Count == 2);
        await _tts.Preparing;

        await Until(() => hung.Disposed);
        _tts.Status.State.ShouldBe(TextToSpeechState.Ready);
        States.ShouldBe([TextToSpeechState.Loading, TextToSpeechState.Ready, TextToSpeechState.Off, TextToSpeechState.Loading, TextToSpeechState.Ready]);
    }

    [Fact]
    public async Task An_answer_during_a_warm_up_gets_the_voice_installed()
    {
        _environment.CheckGate = new TaskCompletionSource();
        _tts.Prepare(install: false); // the warm-up, held while it looks at the disk

        _tts.Prepare(install: true); // the first answer
        _environment.CheckGate.TrySetResult();
        await Until(() => _tts.Status.State == TextToSpeechState.Ready);

        _environment.Installs.ShouldBe(1);
    }

    [Fact]
    public async Task A_sentence_cut_off_by_a_new_model_is_not_a_failure()
    {
        _environment.Installed = true;
        _tts.Prepare(install: false);
        await _tts.Preparing;
        _handler.Body = new byte[64];
        _handler.ReadSize = 16;
        _handler.FailAfterFirstRead = () => _settings.Model = SpeechModel.Large; // the sidecar is killed under the request

        var error = await Should.ThrowAsync<TextToSpeechException>(async () =>
        {
            await foreach (var _ in _tts.SpeakAsync("Hello there.", CancellationToken.None))
            {
            }
        });

        error.ShouldBeOfType<TextToSpeechNotReadyException>("the user changed the model: no \"could not speak\"");
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
        _settings.QwenVoice = "aiden";
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
    public async Task Speaking_before_the_first_install_says_it_installs()
    {
        var error = await Should.ThrowAsync<TextToSpeechNotReadyException>(async () =>
        {
            await foreach (var _ in _tts.SpeakAsync("Hello.", CancellationToken.None))
            {
            }
        });

        error.Status.State.ShouldBe(TextToSpeechState.Installing, "not \"still loading\" while 5 GB are installed");
        await _tts.Preparing;
    }

    [Fact]
    public async Task A_new_model_picked_during_the_install_lets_it_finish_and_is_loaded_after()
    {
        _environment.Gate = new TaskCompletionSource();
        _tts.Prepare(install: true);
        await Until(() => _tts.Status.State == TextToSpeechState.Installing);

        _settings.Model = SpeechModel.Large;
        _environment.Gate.TrySetResult();
        await Until(() => _tts.Status.State == TextToSpeechState.Ready);

        _environment.Installs.ShouldBe(1);
        _environment.Cancelled.ShouldBeFalse();
        _launcher.Starts.ShouldBe(["Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice"]);
    }

    [Fact]
    public async Task A_late_word_of_the_sidecar_about_loading_does_not_undo_ready()
    {
        _environment.Installed = true;
        _tts.Prepare(install: false);
        await _tts.Preparing;
        _tts.Status.State.ShouldBe(TextToSpeechState.Ready);

        _launcher.LastStatus!("downloading the model"); // a Hugging Face line on stderr, read after the ready event

        _tts.Status.State.ShouldBe(TextToSpeechState.Ready);
    }

    [Fact]
    public async Task A_load_a_new_model_cancelled_tells_nothing_more()
    {
        _environment.Installed = true;
        _launcher.Gate = new TaskCompletionSource(); // the first sidecar finishes loading, cancelled or not
        _tts.Prepare(install: false);
        await Until(() => _launcher.Pending is not null); // set just after the start is counted

        _launcher.Gate = null;
        var stale = _launcher.Pending!;
        _settings.Model = SpeechModel.Large; // the second model loads at once
        await Until(() => _tts.Status.State == TextToSpeechState.Ready);
        var told = States.Count;
        stale.TrySetResult();
        await Until(() => _launcher.Servers[0].Disposed);

        States.Count.ShouldBe(told, "the cancelled load says neither Ready nor Failed");
        _tts.Status.State.ShouldBe(TextToSpeechState.Ready);
    }

    [Fact]
    public async Task Getting_ready_never_reads_the_disk_on_the_calling_thread()
    {
        _environment.CheckGate = new TaskCompletionSource(); // a sleeping disk

        var prepare = Task.Run(() => _tts.Prepare(install: false), TestContext.Current.CancellationToken);

        (await Task.WhenAny(prepare, Task.Delay(1000, TestContext.Current.CancellationToken))).ShouldBe(prepare, "unmuting must not wait for the disk");
        _environment.CheckGate.TrySetResult();
        await _tts.Preparing;
    }

    [Fact]
    public async Task A_new_model_never_waits_for_the_old_sidecar_to_be_killed()
    {
        _environment.Installed = true;
        _tts.Prepare(install: false);
        await _tts.Preparing;
        var killing = new TaskCompletionSource();
        _launcher.Servers[0].Killing = killing;

        var change = Task.Run(() => _settings.Model = SpeechModel.Large, TestContext.Current.CancellationToken);

        (await Task.WhenAny(change, Task.Delay(1000, TestContext.Current.CancellationToken))).ShouldBe(change, "Settings must not wait for a process tree to die");
        killing.TrySetResult();
        await Until(() => _launcher.Servers[0].Disposed);
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

        await Until(() => first.Disposed);
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
        States.ShouldBe([TextToSpeechState.Loading, TextToSpeechState.Ready, TextToSpeechState.Off, TextToSpeechState.Loading, TextToSpeechState.Ready]);
    }

    [Fact]
    public async Task Stopping_gives_up_an_install_and_says_it_is_not_installed()
    {
        _environment.Gate = new TaskCompletionSource();
        _tts.Prepare(install: true);
        await Until(() => _tts.Status.State == TextToSpeechState.Installing);

        _tts.Stop();

        await Until(() => _tts.Status.State == TextToSpeechState.NotInstalled);
        await Until(() => _environment.Cancelled);
        _launcher.Starts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stopping_a_running_voice_kills_its_sidecar_and_it_starts_again_when_next_needed()
    {
        _environment.Installed = true;
        _tts.Prepare(install: false);
        await _tts.Preparing;

        _tts.Stop();

        await Until(() => _launcher.Servers[0].Disposed);
        await Until(() => _tts.Status.State == TextToSpeechState.Off);
        _tts.Prepare(install: false);
        await _tts.Preparing;
        _launcher.Starts.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Stopping_a_voice_that_is_off_tells_nothing()
    {
        _tts.Stop();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        States.ShouldBeEmpty();
    }

    [Fact]
    public async Task Checking_the_install_says_whether_the_model_picked_is_on_disk()
    {
        _tts.CheckInstall();
        await Until(() => _tts.Status.State == TextToSpeechState.NotInstalled);

        _environment.Installed = true;
        _environment.Models = ["Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice"];
        _tts.CheckInstall();
        await Until(() => _tts.Status.State == TextToSpeechState.Off);

        _settings.Model = SpeechModel.Large; // not downloaded: its first start would download 3.5 GB
        await Until(() => _tts.Status.State == TextToSpeechState.NotInstalled);
        _launcher.Starts.ShouldBeEmpty("a model picked while the voice is off does not start it");
    }

    [Fact]
    public async Task A_check_of_the_install_never_undoes_a_voice_getting_ready()
    {
        _environment.Installed = true;
        _tts.Prepare(install: false);
        await _tts.Preparing;

        _tts.CheckInstall();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        _tts.Status.State.ShouldBe(TextToSpeechState.Ready);
    }

    [Fact]
    public async Task Another_engine_s_model_leaves_this_one_running()
    {
        using var kokoro = new SidecarTextToSpeech(SpeechEngine.Kokoro, _environment, _launcher, _settings, new HttpClient(_handler), _time,
            NullLogger<SidecarTextToSpeech>.Instance);
        _environment.Installed = true;
        kokoro.Prepare(install: false);
        await kokoro.Preparing;

        _settings.Model = SpeechModel.Large;

        _launcher.Starts.ShouldBe(["kokoro-v1.0"]);
        _launcher.Servers[0].Disposed.ShouldBeFalse();
        kokoro.Status.State.ShouldBe(TextToSpeechState.Ready);
    }

    [Fact]
    public async Task Kokoro_speaks_with_the_Kokoro_voice_of_the_settings()
    {
        using var kokoro = new SidecarTextToSpeech(SpeechEngine.Kokoro, _environment, _launcher, _settings, new HttpClient(_handler), _time,
            NullLogger<SidecarTextToSpeech>.Instance);
        _environment.Installed = true;
        _settings.KokoroVoice = "bm_george";
        kokoro.Prepare(install: false);
        await kokoro.Preparing;
        _handler.Body = [1, 2];

        await foreach (var _ in kokoro.SpeakAsync("Hello there.", CancellationToken.None))
        {
        }

        _handler.Request!.RootElement.GetProperty("voice").GetString().ShouldBe("bm_george");
    }

    [Fact]
    public async Task An_install_asked_for_tries_again_at_once_after_a_failure()
    {
        _environment.InstallFails = true;
        _tts.Prepare(install: true);
        await _tts.Preparing;
        _environment.InstallFails = false;

        _tts.Install();
        await Until(() => _tts.Status.State == TextToSpeechState.Ready);

        _environment.Installs.ShouldBe(2, "the user asked: no waiting out the retry time");
    }

    [Fact]
    public async Task An_install_tells_how_far_its_download_is()
    {
        var statuses = new List<TextToSpeechStatus>();
        _tts.StatusChanged += (_, status) => { lock (statuses) { statuses.Add(status); } };
        _environment.Steps = [new InstallStep("downloading the model", new ByteProgress(142_000_000, 330_000_000))];

        _tts.Prepare(install: true);
        await _tts.Preparing;

        lock (statuses)
        {
            statuses.ShouldContain(new TextToSpeechStatus(TextToSpeechState.Installing, "downloading the model", new ByteProgress(142_000_000, 330_000_000)));
        }
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

    private sealed class FakeEnvironment : ISidecarEnvironment
    {
        public bool Installed { get; set; }

        public bool InstallFails { get; set; }

        public int Installs { get; private set; }

        /// <summary>When set, the install waits for it.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public bool Cancelled { get; private set; }

        /// <summary>When set, looking at the install waits for it (a sleeping disk).</summary>
        public TaskCompletionSource? CheckGate { get; set; }

        public bool IsInstalled
        {
            get
            {
                CheckGate?.Task.Wait();
                return Installed;
            }
        }

        /// <summary>The models on disk; with none named, every one.</summary>
        public HashSet<string>? Models { get; set; }

        public bool HasModel(string model) => Models?.Contains(model) ?? true;

        public string Python => "python.exe";

        public string ModelArgument(string model) => model;

        public IReadOnlyDictionary<string, string> Variables { get; } = new Dictionary<string, string>();

        public string WriteScript() => "server.py";

        /// <summary>What the install tells as it goes.</summary>
        public List<InstallStep> Steps { get; set; } = [];

        public async Task InstallAsync(IProgress<InstallStep> progress, CancellationToken ct)
        {
            Installs++;
            foreach (var step in Steps)
            {
                progress.Report(step);
            }

            if (Gate is { } gate)
            {
                // Not a callback on the token: the wait's own callback runs first and, inline, would dispose it unrun.
                try
                {
                    await gate.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    Cancelled = true;
                    throw;
                }
            }

            if (InstallFails)
            {
                throw new TextToSpeechException("no network");
            }

            Installed = true;
        }
    }

    private sealed class FakeLauncher : ISidecarLauncher
    {
        public List<string> Starts { get; } = [];

        public List<FakeServer> Servers { get; } = [];

        /// <summary>When set, the next start waits for it before it is ready, cancelled or not (see <see cref="HonorsCancel"/>).</summary>
        public TaskCompletionSource? Gate { get; set; }

        /// <summary>A start held by <see cref="Gate"/> gives up when cancelled, as the real one does.</summary>
        public bool HonorsCancel { get; set; }

        /// <summary>The gate the last start waits for.</summary>
        public TaskCompletionSource? Pending { get; private set; }

        public Action<string>? LastStatus { get; private set; }

        public async Task<ISidecarServer> StartAsync(ISidecarEnvironment environment, string model, Action<string> onStatus, CancellationToken ct)
        {
            lock (Starts)
            {
                Starts.Add(model);
            }

            LastStatus = onStatus;
            var server = new FakeServer();
            Servers.Add(server);
            if (Gate is { } gate)
            {
                Pending = gate;
                if (HonorsCancel)
                {
                    try
                    {
                        await gate.Task.WaitAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        server.Dispose();
                        throw;
                    }
                }
                else
                {
                    await gate.Task;
                }
            }

            return server;
        }
    }

    private sealed class FakeServer : ISidecarServer
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Uri Address { get; } = new("http://127.0.0.1:5000/");

        public string Token => "token";

        public Task Exited => _exited.Task;

        public volatile bool Disposed;

        /// <summary>When set, killing it waits for it.</summary>
        public TaskCompletionSource? Killing { get; set; }

        public void End() => _exited.TrySetResult();

        public void Dispose()
        {
            Killing?.Task.Wait();
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

        /// <summary>When set, runs after the first read, and every read after it fails as a killed connection does.</summary>
        public Action? FailAfterFirstRead { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ContentLength = request.Content!.Headers.ContentLength; // before the read below, which buffers it and so gives it one
            Request = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            Authorization = request.Headers.Authorization?.ToString();
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Trickle(Body, ReadSize, FailAfterFirstRead)) };
            response.Headers.Add("X-Sample-Rate", "24000");
            return response;
        }
    }

    /// <summary>Gives at most a few bytes per read, as a network stream may; with a hook, fails after the first read.</summary>
    private sealed class Trickle(byte[] data, int size, Action? failAfterFirstRead = null) : MemoryStream(data)
    {
        private int _reads;

        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, size));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (failAfterFirstRead is not null && _reads++ == 1)
            {
                failAfterFirstRead();
                throw new IOException("An existing connection was forcibly closed by the remote host.");
            }

            return base.ReadAsync(buffer[..Math.Min(buffer.Length, size)], ct);
        }
    }
}
