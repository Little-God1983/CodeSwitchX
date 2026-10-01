using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Voice.Speech.QwenTts;

/// <summary>The Python environment the Qwen3-TTS sidecar runs in; installed on first need.</summary>
public interface IQwenTtsEnvironment
{
    /// <summary>Installed completely, by this version of the app's recipe.</summary>
    bool IsInstalled { get; }

    /// <summary>The environment's Python.</summary>
    string Python { get; }

    /// <summary>Where the models are downloaded to and loaded from (Hugging Face's cache).</summary>
    string ModelCache { get; }

    /// <summary>Writes the sidecar's script into the environment (every start: an update of the app may change it); returns its path.</summary>
    string WriteScript();

    /// <summary>Installs it from scratch: what a failed or older install left is removed first. Several minutes, about 5 GB.</summary>
    /// <param name="progress">What it is doing, in a few words ("downloading PyTorch").</param>
    /// <exception cref="TextToSpeechException">A step failed.</exception>
    Task InstallAsync(IProgress<string> progress, CancellationToken ct);
}

/// <summary>
/// The environment under the app's voice folder, made with uv: a uv-managed Python 3.12 (so a Python installed or removed
/// on the machine never breaks it), PyTorch for CUDA 12.8 (the build that runs on Blackwell cards) and faster-qwen3-tts,
/// whose CUDA graphs make Qwen3-TTS several times faster than real time on Windows (the plain qwen-tts package is three
/// times slower than real time there). Every other package is pinned by <c>constraints.txt</c>, frozen from an environment
/// that ran. uv is the pinned version: the one on the PATH if it is that version, else one downloaded next to the environment.
/// </summary>
public sealed class QwenTtsEnvironment : IQwenTtsEnvironment
{
    /// <summary>Raised whenever the recipe changes (a pin, a step): an environment stamped with another version is installed again.</summary>
    public const string RecipeVersion = "1";

    /// <summary>The recipe relies on this version's flags (<c>--managed-python</c>) and quirks (a path cut at its first space).</summary>
    internal const string UvVersion = "0.11.6";
    private const string PythonVersion = "3.12";
    private const string TorchIndex = "https://download.pytorch.org/whl/cu128";
    private static readonly string[] TorchPackages = ["torch==2.11.0", "torchaudio==2.11.0"];
    private const string EnginePackage = "faster-qwen3-tts==0.5.3";
    private const string ConstraintsFile = "constraints.txt";

    private readonly string _root;
    private readonly IProcessRunner _runner;
    private readonly HttpClient _http;
    private readonly Func<string?> _findUv;
    private readonly ILogger<QwenTtsEnvironment> _logger;

    /// <param name="root">The voice folder: everything the engine needs goes in here.</param>
    /// <param name="findUv">uv of <see cref="UvVersion"/> on the PATH, if any; the pinned uv is downloaded otherwise.</param>
    public QwenTtsEnvironment(string root, string modelCache, IProcessRunner runner, HttpClient http, Func<string?> findUv,
        ILogger<QwenTtsEnvironment> logger)
    {
        _root = root;
        ModelCache = modelCache;
        _runner = runner;
        _http = http;
        _findUv = findUv;
        _logger = logger;
    }

    private string Venv => Path.Combine(_root, "venv");

    private string Stamp => Path.Combine(_root, "installed.txt");

    /// <summary>By version: a recipe that pins another uv downloads that one.</summary>
    private string LocalUv => Path.Combine(_root, "uv", UvVersion, "uv.exe");

    public string Python => Path.Combine(Venv, "Scripts", "python.exe");

    public string ModelCache { get; }

    public bool IsInstalled =>
        File.Exists(Python) && File.Exists(Stamp) && File.ReadAllText(Stamp).Trim() == RecipeVersion;

    public string WriteScript()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "qwen_tts_server.py");
        File.WriteAllText(path, Resource("qwen_tts_server.py"));
        return path;
    }

    public async Task InstallAsync(IProgress<string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_root);
        File.Delete(Stamp);
        if (Directory.Exists(Venv))
        {
            progress.Report("removing the old install");
            Directory.Delete(Venv, recursive: true);
        }

        var uv = _findUv() ?? await DownloadUvAsync(progress, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(_root, ConstraintsFile), Resource(ConstraintsFile), ct).ConfigureAwait(false);

        progress.Report("getting Python " + PythonVersion);
        await UvAsync(uv, ["venv", Venv, "--python", PythonVersion, "--managed-python", "--no-project"], ct).ConfigureAwait(false);

        progress.Report("downloading PyTorch (2.5 GB)");
        await UvAsync(uv, ["pip", "install", "--python", Python, .. TorchPackages, "--index-url", TorchIndex], ct).ConfigureAwait(false);

        progress.Report("downloading Qwen3-TTS");
        // By name, from the voice folder (uv runs there): uv 0.11 cuts a constraints path at its first space, and the
        // data folder is under the user's profile ("C:/Users/Little God/...").
        await UvAsync(uv, ["pip", "install", "--python", Python, EnginePackage, "--constraint", ConstraintsFile], ct).ConfigureAwait(false);

        await File.WriteAllTextAsync(Stamp, RecipeVersion, ct).ConfigureAwait(false);
    }

    private async Task UvAsync(string uv, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var environment = new Dictionary<string, string>
        {
            // Python itself goes into the voice folder, and no cache keeps a second copy of the 5 GB.
            ["UV_PYTHON_INSTALL_DIR"] = Path.Combine(_root, "python"),
            ["UV_NO_CACHE"] = "1",
            ["UV_NO_PROGRESS"] = "1",
            ["NO_COLOR"] = "1",
        };
        _logger.LogInformation("Voice install: uv {Arguments}", string.Join(' ', arguments));
        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(uv, arguments, _root, environment, line => _logger.LogDebug("uv: {Line}", line), ct)
                .ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new TextToSpeechException($"uv could not be started: {ex.Message}", ex);
        }

        if (result.ExitCode != 0)
        {
            _logger.LogWarning("Voice install: uv {Command} failed ({Code}):\n{Output}", arguments[0], result.ExitCode, result.OutputTail);
            var last = result.OutputTail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            throw new TextToSpeechException($"uv {arguments[0]} failed: {last ?? $"exit code {result.ExitCode}"}");
        }
    }

    private async Task<string> DownloadUvAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (File.Exists(LocalUv))
        {
            return LocalUv;
        }

        progress.Report("downloading uv");
        var url = $"https://github.com/astral-sh/uv/releases/download/{UvVersion}/uv-x86_64-pc-windows-msvc.zip";
        try
        {
            await using var download = await _http.GetStreamAsync(url, ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await download.CopyToAsync(buffer, ct).ConfigureAwait(false);
            buffer.Position = 0;
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("uv.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new TextToSpeechException("The uv download holds no uv.exe.");
            Directory.CreateDirectory(Path.GetDirectoryName(LocalUv)!);
            var partial = LocalUv + ".partial";
            entry.ExtractToFile(partial, overwrite: true);
            File.Move(partial, LocalUv, overwrite: true);
            return LocalUv;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException)
        {
            throw new TextToSpeechException($"uv could not be downloaded: {ex.Message}", ex);
        }
    }

    internal static string Resource(string name)
    {
        using var stream = typeof(QwenTtsEnvironment).Assembly.GetManifestResourceStream("CodeSwitchX.Voice.QwenTts." + name)
            ?? throw new InvalidOperationException($"The resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The first uv.exe on the PATH, if it is <see cref="UvVersion"/>: an older one lacks flags the recipe uses.</summary>
    public static string? FindPinnedUvOnPath()
    {
        var uv = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => Path.Combine(folder, "uv.exe"))
            .FirstOrDefault(File.Exists);
        return uv is not null && IsPinnedVersion(VersionOf(uv)) ? uv : null;
    }

    /// <summary>What <c>uv --version</c> says ("uv 0.11.6 (65950801c 2026-04-09 ...)"); empty if it says nothing in 5 s.</summary>
    private static string VersionOf(string uv)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(uv, "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });
            if (process is null)
            {
                return "";
            }

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill();
                return "";
            }

            return output.Result.Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "";
        }
    }

    internal static bool IsPinnedVersion(string versionOutput) =>
        versionOutput == $"uv {UvVersion}" || versionOutput.StartsWith($"uv {UvVersion} ", StringComparison.Ordinal);
}
