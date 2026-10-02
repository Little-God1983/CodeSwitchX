namespace CodeSwitchX.Voice.Tests.Speech;

using System.Net;
using CodeSwitchX.Voice.Speech;
using CodeSwitchX.Voice.Speech.Kokoro;
using CodeSwitchX.Voice.Speech.Sidecar;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class KokoroEnvironmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csx-kokoro-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunner _runner = new();
    private readonly FileHandler _files = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Models => Path.Combine(_root, "models");

    private KokoroEnvironment Environment() =>
        new(Path.Combine(_root, "voice", "kokoro"), Models, new Uv(Path.Combine(_root, "voice"), _runner, new HttpClient(), () => "uv.exe", NullLogger.Instance),
            new HttpClient(_files))
        {
            Files = [("kokoro-v1.0.onnx", 3_000_000), ("voices-v1.0.bin", 1_000_000)],
        };

    [Fact]
    public async Task An_install_makes_a_managed_python_then_kokoro_within_the_pins_then_fetches_the_model()
    {
        var environment = Environment();
        var steps = new List<InstallStep>();

        await environment.InstallAsync(new SyncProgress(steps.Add), CancellationToken.None);

        _runner.Calls.Select(c => c.Arguments[0] + " " + c.Arguments[1]).ShouldBe(["venv " + Path.Combine(_root, "voice", "kokoro", "venv"), "pip install"]);
        _runner.Calls[0].Arguments.ShouldContain("--managed-python");
        _runner.Calls[1].Arguments.ShouldContain("kokoro-onnx==0.6.1");
        _runner.Calls[1].Arguments[^1].ShouldBe("constraints.txt", "by name: uv cuts a path at its first space");
        File.ReadAllText(Path.Combine(_root, "voice", "kokoro", "constraints.txt")).ShouldContain("onnxruntime==1.30.0");
        _runner.Calls.ShouldAllBe(c => c.Environment["UV_PYTHON_INSTALL_DIR"] == Path.Combine(_root, "voice", "python"), "one Python for every engine");
        _files.Asked.ShouldBe([
            "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/kokoro-v1.0.onnx",
            "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/voices-v1.0.bin",
        ]);
        new FileInfo(Path.Combine(Models, "kokoro-v1.0.onnx")).Length.ShouldBe(3_000_000);
        environment.HasModel(KokoroEnvironment.Model).ShouldBeTrue();
        steps.Where(s => s.Bytes is not null).Select(s => s.Bytes!.Value.Done / 1_000_000).ShouldBe([0, 1, 2, 3, 4], "once a megabyte, over both files");
        steps.Last().Bytes.ShouldBe(new ByteProgress(4_000_000, 4_000_000));
    }

    [Fact]
    public async Task Only_a_complete_install_with_its_whole_model_counts_as_installed()
    {
        var environment = Environment();
        await environment.InstallAsync(new Progress<InstallStep>(), CancellationToken.None);
        environment.IsInstalled.ShouldBeFalse(); // the fake made no python.exe

        Directory.CreateDirectory(Path.GetDirectoryName(environment.Python)!);
        File.WriteAllText(environment.Python, "");
        environment.IsInstalled.ShouldBeTrue();

        File.WriteAllBytes(Path.Combine(Models, "voices-v1.0.bin"), new byte[10]);
        environment.IsInstalled.ShouldBeFalse("a model file of another size is a download cut off");
    }

    [Fact]
    public async Task A_model_already_whole_is_not_fetched_again()
    {
        Directory.CreateDirectory(Models);
        File.WriteAllBytes(Path.Combine(Models, "kokoro-v1.0.onnx"), new byte[3_000_000]);

        await Environment().InstallAsync(new Progress<InstallStep>(), CancellationToken.None);

        _files.Asked.ShouldBe(["https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/voices-v1.0.bin"]);
    }

    [Fact]
    public async Task A_model_file_gone_from_a_whole_environment_is_fetched_alone()
    {
        var environment = Environment();
        await environment.InstallAsync(new Progress<InstallStep>(), CancellationToken.None);
        Directory.CreateDirectory(Path.GetDirectoryName(environment.Python)!);
        File.WriteAllText(environment.Python, "");
        File.Delete(Path.Combine(Models, "voices-v1.0.bin"));
        environment.IsInstalled.ShouldBeFalse();
        _runner.Calls.Clear();
        _files.Asked.Clear();

        await environment.InstallAsync(new Progress<InstallStep>(), CancellationToken.None);

        _runner.Calls.ShouldBeEmpty("the environment stays: no new venv, no pip install");
        _files.Asked.ShouldBe(["https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/voices-v1.0.bin"]);
        environment.IsInstalled.ShouldBeTrue();
    }
    [Fact]
    public async Task A_download_cut_off_fails_the_install_and_leaves_no_file()
    {
        _files.CutAfter = 500_000;

        var error = await Should.ThrowAsync<TextToSpeechException>(() => Environment().InstallAsync(new Progress<InstallStep>(), CancellationToken.None));

        error.Message.ShouldContain("ended early");
        Directory.EnumerateFiles(Models).ShouldBeEmpty();
    }

    [Fact]
    public void The_sidecar_is_told_the_model_folder_and_written_with_the_server_it_runs_under()
    {
        var environment = Environment();

        environment.ModelArgument(KokoroEnvironment.Model).ShouldBe(Models);
        var script = environment.WriteScript();
        File.ReadAllText(script).ShouldContain("kokoro_onnx");
        File.Exists(Path.Combine(Path.GetDirectoryName(script)!, "tts_sidecar.py")).ShouldBeTrue();
    }

    private sealed class SyncProgress(Action<InstallStep> report) : IProgress<InstallStep>
    {
        public void Report(InstallStep value) => report(value);
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public List<(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string> environment, Action<string>? onLine, CancellationToken ct)
        {
            if (arguments is ["--version"])
            {
                onLine?.Invoke("uv 0.11.6");
                return Task.FromResult(new ProcessResult(0, "uv 0.11.6"));
            }

            Calls.Add((executable, arguments, workingDirectory, environment));
            return Task.FromResult(new ProcessResult(0, ""));
        }
    }

    /// <summary>Serves zeros as long as the file asked for is in the test's list says, or fewer when cut.</summary>
    private sealed class FileHandler : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        public long? CutAfter { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Asked.Add(url);
            long size = url.EndsWith(".onnx", StringComparison.Ordinal) ? 3_000_000 : 1_000_000;
            var body = new byte[CutAfter ?? size];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }
}
