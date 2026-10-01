namespace CodeSwitchX.Voice.Tests.Speech;

using CodeSwitchX.Voice.Speech;
using CodeSwitchX.Voice.Speech.QwenTts;
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

    private QwenTtsEnvironment Environment() =>
        new(_root, Path.Combine(_root, "hf"), _runner, new HttpClient(), () => "uv.exe", NullLogger<QwenTtsEnvironment>.Instance);

    [Fact]
    public async Task An_install_makes_a_managed_python_then_cuda_torch_then_the_engine_within_the_pins()
    {
        var environment = Environment();
        environment.IsInstalled.ShouldBeFalse();

        await environment.InstallAsync(new Progress<string>(), CancellationToken.None);

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
        await environment.InstallAsync(new Progress<string>(), CancellationToken.None);
        environment.IsInstalled.ShouldBeFalse(); // the fake made no python.exe

        Directory.CreateDirectory(Path.GetDirectoryName(environment.Python)!);
        File.WriteAllText(environment.Python, "");
        environment.IsInstalled.ShouldBeTrue();

        _runner.FailAt = _runner.Calls.Count + 2; // the third step of the next install
        await Should.ThrowAsync<TextToSpeechException>(() => environment.InstallAsync(new Progress<string>(), CancellationToken.None));
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
        QwenTtsEnvironment.IsPinnedVersion(versionOutput).ShouldBe(used);
    }

    [Fact]
    public void The_sidecar_script_is_written_into_the_voice_folder()
    {
        var script = Environment().WriteScript();
        File.ReadAllText(script).ShouldContain("/v1/audio/speech");
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public List<(IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment)> Calls { get; } = [];

        public int FailAt { get; set; } = -1;

        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string> environment, Action<string>? onLine, CancellationToken ct)
        {
            Calls.Add((arguments, workingDirectory, environment));
            return Task.FromResult(FailAt == Calls.Count - 1 ? new ProcessResult(1, "error: no space left") : new ProcessResult(0, ""));
        }
    }
}
