using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Voice.Speech.Sidecar;

/// <summary>
/// uv, which makes the engines' Python environments: the pinned version, the one on the PATH if it is that version, else
/// one downloaded into the voice folder. The Python it gets is uv-managed and lives in the voice folder too (so a Python
/// installed or removed on the machine never breaks an engine), one for every engine.
/// </summary>
public sealed class Uv
{
    /// <summary>The recipes rely on this version's flags (<c>--managed-python</c>) and quirks (a path cut at its first space).</summary>
    internal const string Version = "0.11.6";
    public const string PythonVersion = "3.12";

    private readonly string _voiceRoot;
    private readonly IProcessRunner _runner;
    private readonly HttpClient _http;
    private readonly Func<string?> _findOnPath;
    private readonly ILogger _logger;

    /// <param name="voiceRoot">The voice folder: uv and Python go in here.</param>
    /// <param name="findOnPath">uv on the PATH, if any: used if it is <see cref="Version"/>, else the pinned uv is downloaded.</param>
    public Uv(string voiceRoot, IProcessRunner runner, HttpClient http, Func<string?> findOnPath, ILogger logger)
    {
        _voiceRoot = voiceRoot;
        _runner = runner;
        _http = http;
        _findOnPath = findOnPath;
        _logger = logger;
    }

    /// <summary>By version: a recipe that pins another uv downloads that one.</summary>
    private string LocalUv => Path.Combine(_voiceRoot, "uv", Version, "uv.exe");

    /// <summary>The pinned uv: the one on the PATH, or one downloaded.</summary>
    /// <exception cref="TextToSpeechException">It could not be downloaded.</exception>
    public async Task<string> FindAsync(IProgress<InstallStep> progress, CancellationToken ct) =>
        await PinnedOnPathAsync(ct).ConfigureAwait(false) ?? await DownloadAsync(progress, ct).ConfigureAwait(false);

    /// <summary>Makes a virtual environment at <paramref name="venv"/> with uv's Python <see cref="PythonVersion"/>.</summary>
    public Task CreateVenvAsync(string uv, string workingDirectory, string venv, CancellationToken ct) =>
        RunAsync(uv, workingDirectory, ["venv", venv, "--python", PythonVersion, "--managed-python", "--no-project"], ct);

    /// <exception cref="TextToSpeechException">uv could not be started, or failed.</exception>
    public async Task RunAsync(string uv, string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var environment = new Dictionary<string, string>
        {
            // Python itself goes into the voice folder, and no cache keeps a second copy of what is installed.
            ["UV_PYTHON_INSTALL_DIR"] = Path.Combine(_voiceRoot, "python"),
            ["UV_NO_CACHE"] = "1",
            ["UV_NO_PROGRESS"] = "1",
            ["NO_COLOR"] = "1",
        };
        _logger.LogInformation("Voice install: uv {Arguments}", string.Join(' ', arguments));
        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(uv, arguments, workingDirectory, environment, line => _logger.LogDebug("uv: {Line}", line), ct)
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

    private async Task<string> DownloadAsync(IProgress<InstallStep> progress, CancellationToken ct)
    {
        if (File.Exists(LocalUv))
        {
            return LocalUv;
        }

        progress.Report(new("downloading uv"));
        var url = $"https://github.com/astral-sh/uv/releases/download/{Version}/uv-x86_64-pc-windows-msvc.zip";
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

    /// <summary>uv.exe on the PATH, if any.</summary>
    public static string? FindOnPath() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => Path.Combine(folder, "uv.exe"))
            .FirstOrDefault(File.Exists);

    /// <summary>The uv on the PATH if it says it is <see cref="Version"/> within 5 s: an older one lacks flags the recipes use.</summary>
    private async Task<string?> PinnedOnPathAsync(CancellationToken ct)
    {
        if (_findOnPath() is not { } uv)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var said = "";
        Directory.CreateDirectory(_voiceRoot);
        try
        {
            await _runner.RunAsync(uv, ["--version"], _voiceRoot, new Dictionary<string, string>(), line => said = said.Length == 0 ? line.Trim() : said,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Said nothing in time.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Not runnable.
        }

        if (IsPinnedVersion(said))
        {
            return uv;
        }

        _logger.LogInformation("Voice install: the uv on the PATH says \"{Version}\", not {Pinned}; the pinned one is used", said, Version);
        return null;
    }

    internal static bool IsPinnedVersion(string versionOutput) =>
        versionOutput == $"uv {Version}" || versionOutput.StartsWith($"uv {Version} ", StringComparison.Ordinal);
}
