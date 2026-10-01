using System.Diagnostics;

namespace CodeSwitchX.Core.Tests;

/// <summary>
/// scripts\_common.ps1, build.ps1 and build.cmd: the stable build that bumps the version, publishes into
/// E:\StableVersion and pushes main. The helpers are run in a real pwsh, dot-sourced the way build.ps1 does
/// it, because the rules worth pinning - which version comes next, which folder may be deleted - live in
/// PowerShell, not in C#.
/// </summary>
public class BuildScriptTests
{
    [Theory]
    [InlineData("0.1.0.10", "build", "0.1.0.11")]
    [InlineData("0.1.0", "build", "0.1.0.1")]
    [InlineData("0.1.0.10", "patch", "0.1.1")]
    [InlineData("0.1.0.10", "minor", "0.2.0")]
    [InlineData("0.1.0.10", "major", "1.0.0")]
    public void The_next_version_zeroes_everything_right_of_the_part(string current, string part, string next)
    {
        var result = Pwsh.RunCommon($"Get-NextVersion '{current}' '{part}'");

        result.ExitCode.ShouldBe(0);
        result.Output.Trim().ShouldBe(next);
    }

    [Theory]
    [InlineData("0123456")]
    [InlineData("1234567")]
    [InlineData("f31a120")]
    [InlineData("")]
    public void A_one_off_stamp_is_never_a_numeric_prerelease_identifier(string commit)
    {
        // NuGet's restore rejects a SemVer prerelease identifier that is all digits with a leading zero:
        // -p:Version=0.1.0.10-oneoff.0123456 fails with MSB4181, about 1 commit in 270. A letter in front
        // makes every hash an alphanumeric identifier, which has no such rule.
        var result = Pwsh.RunCommon($"Get-OneOffVersion '0.1.0.10' '{commit}'");

        result.Output.Trim().ShouldBe(commit.Length == 0 ? "0.1.0.10-oneoff.nogit" : $"0.1.0.10-oneoff.g{commit}");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(128, 1)]
    public void A_failing_git_status_is_not_a_clean_tree(int statusExitCode, int expectedExitCode)
    {
        // A corrupt index or a safe.directory refusal makes git status print nothing to stdout and exit
        // non-zero. Empty output on its own reads as "no changes", so the exit code has to be what decides.
        var fakeGit =
            "function Invoke-Git { param([string[]]$Arguments, [switch]$Quiet) " +
            "$global:LASTEXITCODE = 0; " +
            "switch ($Arguments[0]) { 'rev-parse' { 'main' } " +
            $"'status' {{ $global:LASTEXITCODE = {statusExitCode} }} " +
            "'rev-list' { \"0`t0\" } } }";

        var result = Pwsh.RunCommon($"{fakeGit}; Assert-ReleaseReady");

        result.ExitCode.ShouldBe(expectedExitCode);
        if (expectedExitCode != 0)
        {
            result.Output.ShouldContain("git status failed");
        }
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void A_finished_stable_folder_is_never_republished_over(bool complete, int expectedExitCode)
    {
        // The junction and the Start Menu shortcut move to the new folder before the bump is committed and
        // pushed. A push that fails, followed by a reset to origin, hands out the same number again - and
        // its folder is then the one the shortcut opens, with CodeSwitchX running from it.
        // A folder that never got that far was never made current, so it may go.
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "CodeSwitchX-0.1.0.11");
        var marker = complete ? "-Complete" : "";

        var result = Pwsh.RunCommon(
            $"Write-InstallMarker '{folder}' '0.1.0.11' {marker}; Assert-StableTargetFree '{folder}' '0.1.0.11' '0.1.0.10'");

        result.ExitCode.ShouldBe(expectedExitCode);
        if (complete)
        {
            result.Output.ShouldContain("already published");
        }
    }

    [Theory]
    [InlineData("0.1.0.10", false)]
    [InlineData("0.1.0.10-oneoff.gf31a120", true)]
    [InlineData("0.1.0.10-oneoff.f31a120", true)]
    public void A_one_off_build_only_reuses_a_one_off_folder(string markedVersion, bool reusable)
    {
        // The "outside the stable root" check compares path strings, so an 8.3 name (E:\STABLE~1\...), a
        // junction or a subst drive leads into E:\StableVersion without matching it. What the marker says
        // cannot be aliased: a stable release is never a one-off's folder.
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "CodeSwitchX-test");

        var result = Pwsh.RunCommon(
            $"Write-InstallMarker '{folder}' '{markedVersion}' -Complete; Test-OurInstall '{folder}' -OneOff");

        result.Output.Trim().ShouldBe(reusable.ToString(), StringCompareShould.IgnoreCase);
    }

    [Fact]
    public void A_double_clicked_build_waits_even_when_it_succeeds()
    {
        // Explorer runs a double-clicked .cmd as cmd /c "<path>", in a window that closes the moment the
        // script ends. The end of a successful build carries warnings - another CodeSwitchX left running,
        // a shortcut that still opens the previous build - so that window has to wait. -? is a quick success.
        var result = Cmd.Run($"/c \"\"{Path.Combine(Pwsh.ScriptsFolder, "build.cmd")}\" -?\"", stdin: "");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("Press any key");
    }

    [Fact]
    public void A_build_started_from_a_console_does_not_wait_on_success()
    {
        // Typed into a console that stays open, the output is already on screen to read.
        var result = Cmd.Run("/q", stdin: $"call \"{Path.Combine(Pwsh.ScriptsFolder, "build.cmd")}\" -?\r\nexit\r\n");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldNotContain("Press any key");
    }

    private sealed record ScriptResult(int ExitCode, string Output);

    private static class Cmd
    {
        public static ScriptResult Run(string arguments, string stdin) => Shell.Run("cmd.exe", arguments, stdin);
    }

    private static class Pwsh
    {
        public static string ScriptsFolder { get; } = Path.Combine(FindRepoRoot(), "scripts");

        /// <summary>Runs <paramref name="command"/> after dot-sourcing _common.ps1, as build.ps1 does.</summary>
        public static ScriptResult RunCommon(string command) =>
            Shell.Run(
                "pwsh",
                "-NoProfile -NonInteractive -Command -",
                $". '{Path.Combine(ScriptsFolder, "_common.ps1")}'\n{command}\nexit $LASTEXITCODE\n");

        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodeSwitchX.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("CodeSwitchX.slnx not found above the test output.");
        }
    }

    private static class Shell
    {
        public static ScriptResult Run(string fileName, string arguments, string stdin)
        {
            var start = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(start)!;
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return new ScriptResult(process.ExitCode, stdout + stderr.Result);
        }
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeSwitchX.BuildScriptTests-" + Guid.NewGuid())).FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup: a lingering handle must not fail the suite.
            }
        }
    }
}
