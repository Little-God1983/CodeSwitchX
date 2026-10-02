using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Hosting.VsCode.Companion;

/// <summary>Puts the companion into VS Code.</summary>
public interface ICompanionInstaller
{
    /// <summary>
    /// Installs the shipped companion into each profile (null: VS Code's default) where it is missing or another version.
    /// Says which profiles got it and which could not; never throws but for a cancel.
    /// </summary>
    Task<CompanionInstall> EnsureAsync(IEnumerable<string?> profiles, CancellationToken ct);
}

/// <param name="Installed">The profiles it was installed into now ("default" for VS Code's own).</param>
/// <param name="Failed">The profiles it could not be installed into, each with why.</param>
public sealed record CompanionInstall(IReadOnlyList<string> Installed, IReadOnlyList<string> Failed);

/// <summary>
/// Installs the companion with VS Code's own command line, as <c>code --install-extension</c> does: VS Code's
/// <c>Code.exe</c> run as Node on its <c>cli.js</c>. A window that is open already starts the extension within seconds,
/// without a reload. Only one install runs at a time, and a profile already up to date is not touched.
/// </summary>
public sealed class CompanionInstaller : ICompanionInstaller
{
    /// <summary>How long one call of VS Code's command line may take; it takes about a second.</summary>
    internal static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(60);

    private readonly CompanionPackage _package;
    private readonly string _vsixPath;
    private readonly Func<string?> _locate;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<(int Exit, string Output)>> _run;
    private readonly ILogger<CompanionInstaller> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="vsixDirectory">Where the .vsix is written before it is installed.</param>
    public CompanionInstaller(string vsixDirectory, ILogger<CompanionInstaller> logger)
        : this(CompanionPackage.Shipped, vsixDirectory, VsCodeLocator.FindExecutable, null, logger)
    {
    }

    /// <param name="locate">Where Code.exe is.</param>
    /// <param name="run">Runs VS Code's command line with these arguments (after cli.js); its exit code and output.</param>
    internal CompanionInstaller(CompanionPackage package, string vsixDirectory, Func<string?> locate,
        Func<IReadOnlyList<string>, CancellationToken, Task<(int Exit, string Output)>>? run, ILogger<CompanionInstaller> logger)
    {
        _package = package;
        _vsixPath = Path.Combine(vsixDirectory, $"codeswitchx-companion-{package.Version}.vsix");
        _locate = locate;
        _run = run ?? RunCliAsync;
        _logger = logger;
    }

    public async Task<CompanionInstall> EnsureAsync(IEnumerable<string?> profiles, CancellationToken ct)
    {
        var wanted = profiles.Select(p => string.IsNullOrWhiteSpace(p) ? null : p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var installed = new List<string>();
        var failed = new List<string>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var vsixWritten = false;
            foreach (var profile in wanted)
            {
                var label = profile ?? "default";
                try
                {
                    var list = await _run([.. WithProfile(["--list-extensions", "--show-versions"], profile)], ct).ConfigureAwait(false);
                    if (list.Exit == 0 && InstalledVersion(list.Output) == _package.Version)
                    {
                        continue;
                    }

                    if (!vsixWritten)
                    {
                        _package.WriteVsix(_vsixPath);
                        vsixWritten = true;
                    }

                    var install = await _run([.. WithProfile(["--install-extension", _vsixPath, "--force"], profile)], ct).ConfigureAwait(false);
                    if (install.Exit == 0)
                    {
                        installed.Add(label);
                        _logger.LogInformation("Installed the VS Code companion {Version} into the {Profile} profile", _package.Version, label);
                    }
                    else
                    {
                        failed.Add($"{label}: {LastLine(install.Output)}");
                        _logger.LogWarning("Installing the VS Code companion into the {Profile} profile failed ({Exit}): {Output}", label, install.Exit, install.Output);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    failed.Add($"{label}: {ex.Message}");
                    _logger.LogWarning(ex, "Installing the VS Code companion into the {Profile} profile failed", label);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return new CompanionInstall(installed, failed);
    }

    private static IEnumerable<string> WithProfile(IEnumerable<string> arguments, string? profile) =>
        profile is null ? arguments : arguments.Concat(["--profile", profile]);

    /// <summary>The companion's version in <c>--list-extensions --show-versions</c> output (<c>codeswitchx.companion@0.1.0</c>); null when it is not there.</summary>
    internal static string? InstalledVersion(string output) =>
        output.Split('\n').Select(l => l.Trim())
            .Where(l => l.StartsWith(CompanionPackage.ExtensionId + "@", StringComparison.OrdinalIgnoreCase))
            .Select(l => l[(CompanionPackage.ExtensionId.Length + 1)..])
            .FirstOrDefault();

    private static string LastLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no output";

    /// <summary>
    /// Code.exe as Node on the cli.js its bin\code.cmd names: VS Code keeps it in a folder named after its build, which
    /// changes with each update, so the shim is read rather than the folder guessed.
    /// </summary>
    private async Task<(int Exit, string Output)> RunCliAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var executable = _locate() ?? throw new InvalidOperationException("VS Code was not found.");
        var cli = CliScript(executable) ?? throw new InvalidOperationException($"VS Code's command line was not found next to {executable}.");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(cli);
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // As code.cmd sets them. A CLI that inherits a VS Code terminal's hook would hand the command to that window.
        info.Environment["ELECTRON_RUN_AS_NODE"] = "1";
        info.Environment.Remove("VSCODE_DEV");
        info.Environment.Remove("VSCODE_IPC_HOOK_CLI");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CliTimeout);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("VS Code's command line did not start.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }

    /// <summary>The cli.js that bin\code.cmd next to <paramref name="codeExe"/> runs; the old layout's resources\app\out\cli.js when the shim names none.</summary>
    internal static string? CliScript(string codeExe)
    {
        var root = Path.GetDirectoryName(codeExe)!;
        var shim = Path.Combine(root, "bin", "code.cmd");
        if (File.Exists(shim)
            && Regex.Match(File.ReadAllText(shim), @"%~dp0\.\.\\(?<path>[^""%]*?resources\\app\\out\\cli\.js)", RegexOptions.IgnoreCase) is { Success: true } named
            && Path.Combine(root, named.Groups["path"].Value) is var script && File.Exists(script))
        {
            return script;
        }

        var old = Path.Combine(root, "resources", "app", "out", "cli.js");
        return File.Exists(old) ? old : null;
    }
}
