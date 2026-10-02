namespace CodeSwitchX.Voice.Tests.Speech;

using CodeSwitchX.Voice.Speech;
using CodeSwitchX.Voice.Speech.QwenTts;
using CodeSwitchX.Voice.Speech.Sidecar;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class QwenTtsEnvironmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csx-voice-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunner _runner = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private QwenTtsEnvironment Environment(HttpMessageHandler? download = null) =>
        new(_root, Path.Combine(_root, "hf"), new Uv(_root, _runner, new HttpClient(download ?? new ZipHandler()), () => "uv.exe", NullLogger.Instance));

    [Fact]
    public async Task An_install_makes_a_managed_python_then_cuda_torch_then_the_engine_within_the_pins()
    {
        var environment = Environment();
        environment.IsInstalled.ShouldBeFalse();

        await environment.InstallAsync(new Progress<InstallStep>(), CancellationToken.None);

        _runner.Calls.Select(c => c.Arguments[0] + " " + c.Arguments[1]).ShouldBe(["venv " + Path.Combine(_root, "venv"), "pip install", "pip install"]);
        _runner.Calls[0].Arguments.ShouldContain("--managed-python");
        _runner.Calls[1].Arguments.ShouldContain("torch==2.11.0");
        _runner.Calls[1].Arguments.ShouldContain("https://download.pytorch.org/whl/cu128");
        _runner.Calls[2].Arguments.ShouldContain("faster-qwen3-tts==0.5.3");
        _runner.Calls[2].Arguments[^1].ShouldBe("constraints.txt", "by name: uv cuts a path at its first space");
        _runner.Calls.ShouldAllBe(c => c.WorkingDirectory == _root);
        File.ReadAllText(Path.Combine(_root, "constraints.txt")).ShouldContain("transformers==5.15.1");
        _runner.Calls.ShouldAllBe(c => c.Environment["UV_PYTHON_INSTALL_DIR"] == Path.Combine(_root, "python"));
    }

    [Fact]
    public async Task Only_a_complete_install_counts_as_installed()
    {
        var environment = Environment();
        await environment.InstallAsync(new Progress<InstallStep>(), CancellationToken.None);
        environment.IsInstalled.ShouldBeFalse(); // the fake made no python.exe

        Directory.CreateDirectory(Path.GetDirectoryName(environment.Python)!);
        File.WriteAllText(environment.Python, "");
        environment.IsInstalled.ShouldBeTrue();

        _runner.FailAt = _runner.Calls.Count + 2; // the third step of the next install
        await Should.ThrowAsync<TextToSpeechException>(() => environment.InstallAsync(new Progress<InstallStep>(), CancellationToken.None));
        environment.IsInstalled.ShouldBeFalse();
    }

    [Theory]
    [InlineData("uv 0.11.6 (65950801c 2026-04-09 x86_64-pc-windows-msvc)", true)]
    [InlineData("uv 0.11.6", true)]
    [InlineData("uv 0.5.31 (abc 2024-12-01)", false)]
    [InlineData("uv 0.11.60 (abc)", false)]
    [InlineData("", false)]
    public void Only_the_pinned_uv_on_the_path_is_used(string versionOutput, bool used)
    {
        Uv.IsPinnedVersion(versionOutput).ShouldBe(used);
    }

    [Fact]
    public async Task The_uv_on_the_path_is_used_when_it_is_the_pinned_version()
    {
        _runner.VersionOutput = "uv 0.11.6 (65950801c 2026-04-09 x86_64-pc-windows-msvc)";

        await Environment().InstallAsync(new Progress<InstallStep>(), CancellationToken.None);

        _runner.Calls.ShouldAllBe(c => c.Executable == "uv.exe");
    }

    [Fact]
    public async Task An_older_uv_on_the_path_is_passed_over_for_the_pinned_one()
    {
        _runner.VersionOutput = "uv 0.5.31 (abc 2024-12-01)";
        var download = new ZipHandler();

        await Environment(download).InstallAsync(new Progress<InstallStep>(), CancellationToken.None);

        download.Asked.ShouldBe(["https://github.com/astral-sh/uv/releases/download/0.11.6/uv-x86_64-pc-windows-msvc.zip"]);
        _runner.Calls.ShouldAllBe(c => c.Executable == Path.Combine(_root, "uv", "0.11.6", "uv.exe"));
    }

    [Fact]
    public void The_sidecar_script_is_written_into_the_voice_folder_with_the_server_it_runs_under()
    {
        var script = Environment().WriteScript();

        script.ShouldBe(Path.Combine(_root, "qwen_tts_server.py"));
        File.ReadAllText(script).ShouldContain("tts_sidecar.run(load, speak");
        File.ReadAllText(Path.Combine(_root, "tts_sidecar.py")).ShouldContain("/v1/audio/speech");
    }

    [Fact]
    public void A_model_counts_as_downloaded_once_its_weights_are_in_the_cache_and_nothing_is_still_downloading()
    {
        var environment = Environment();
        const string model = "Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice";
        var folder = Path.Combine(_root, "hf", "hub", "models--Qwen--Qwen3-TTS-12Hz-0.6B-CustomVoice");
        var snapshot = Path.Combine(folder, "snapshots", "85e237c1");
        Directory.CreateDirectory(snapshot);
        File.WriteAllText(Path.Combine(snapshot, "config.json"), "{}");
        environment.HasModel(model).ShouldBeFalse("the weights are not there yet");

        File.WriteAllText(Path.Combine(snapshot, "model.safetensors"), "");
        environment.HasModel(model).ShouldBeTrue();
        environment.HasModel("Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice").ShouldBeFalse();

        Directory.CreateDirectory(Path.Combine(folder, "blobs"));
        File.WriteAllText(Path.Combine(folder, "blobs", "abc.incomplete"), "");
        environment.HasModel(model).ShouldBeFalse("a download cut off is finished by the next start");
    }

    [Fact]
    public void The_sidecar_downloads_its_models_into_the_app_s_cache()
    {
        Environment().Variables["HF_HOME"].ShouldBe(Path.Combine(_root, "hf"));
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public List<(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment)> Calls { get; } = [];

        /// <summary>What <c>uv --version</c> says; not counted among the calls.</summary>
        public string VersionOutput { get; set; } = "uv 0.11.6 (65950801c 2026-04-09 x86_64-pc-windows-msvc)";

        public int FailAt { get; set; } = -1;

        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string> environment, Action<string>? onLine, CancellationToken ct)
        {
            if (arguments is ["--version"])
            {
                onLine?.Invoke(VersionOutput);
                return Task.FromResult(new ProcessResult(0, VersionOutput));
            }

            Calls.Add((executable, arguments, workingDirectory, environment));
            return Task.FromResult(FailAt == Calls.Count - 1 ? new ProcessResult(1, "error: no space left") : new ProcessResult(0, ""));
        }
    }
    /// <summary>Serves a zip holding a uv.exe, as GitHub's release does.</summary>
    private sealed class ZipHandler : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(request.RequestUri!.ToString());
            var zip = new MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            using (var writer = new StreamWriter(archive.CreateEntry("uv-x86_64-pc-windows-msvc/uv.exe").Open()))
            {
                writer.Write("not really uv");
            }

            zip.Position = 0;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(zip) });
        }
    }
}
