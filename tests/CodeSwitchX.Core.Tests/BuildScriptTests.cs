using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace CodeSwitchX.Core.Tests;

/// <summary>
/// scripts\_common.ps1, build.ps1 and build.cmd: the stable build that bumps the version, publishes into
/// E:\StableVersion and pushes main. They are run in a real pwsh and a real cmd, because the rules worth
/// pinning - which version comes next, which folder may be deleted - live in PowerShell, not in C#. The
/// helpers are dot-sourced the way build.ps1 does it; build.ps1 itself runs in a <see cref="FakeRepo"/>.
/// </summary>
public class BuildScriptTests
{
    private const string PauseLine = "Press any key to continue";

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
        // -p:Version=0.1.0.10-oneoff.0123456 fails with MSB4181, about 1 commit in 270. The rule itself is
        // checked, not the spelling: every dot-separated part after the dash is a number without a leading
        // zero, or has a letter or a dash in it.
        var result = Pwsh.RunCommon($"Get-OneOffVersion '0.1.0.10' '{commit}'");

        var stamp = result.Output.Trim();
        stamp.ShouldStartWith("0.1.0.10-oneoff.");
        stamp.ShouldEndWith(commit);
        foreach (var identifier in stamp[(stamp.IndexOf('-') + 1)..].Split('.'))
        {
            identifier.ShouldMatch(@"^(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)$");
        }
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
    [InlineData("chore: bump version to 0.1.0.11", "'Directory.Build.props'", true)]
    [InlineData("chore: bump version to 0.1.0.11", "'Directory.Build.props', 'src/Program.cs'", false)]
    [InlineData("feat: something else", "'Directory.Build.props'", false)]
    public void An_unpushed_bump_is_to_be_pushed_not_reset_away(string subject, string files, bool isTheBump)
    {
        // After a rejected push the bump commit is the one commit main is ahead by. Its build is already
        // installed, so "reset --hard origin/main" - right for any other stray commit - is the advice that
        // hands the same version number out twice.
        var fakeGit =
            "function Invoke-Git { param([string[]]$Arguments, [switch]$Quiet) " +
            "$global:LASTEXITCODE = 0; " +
            "switch ($Arguments[0]) { 'rev-parse' { 'main' } 'rev-list' { \"1`t0\" } " +
            $"'log' {{ '{subject}' }} 'diff' {{ {files} }} }} }}";

        var result = Pwsh.RunCommon($"{fakeGit}; Assert-ReleaseReady");

        result.ExitCode.ShouldBe(1);
        result.Output.Contains("was never pushed").ShouldBe(isTheBump);
        result.Output.Contains("reset --hard").ShouldBe(!isTheBump);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void A_finished_stable_folder_is_never_republished_over(bool complete, int expectedExitCode)
    {
        // The junction and the Start Menu shortcut move to the new folder before the bump is committed and
        // pushed. A bump that is then lost hands out the same number again - and its folder is then the one
        // the shortcut opens, with CodeSwitchX running from it.
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

    [Fact]
    public void The_folder_the_current_link_points_at_is_never_republished_over_whatever_its_marker_says()
    {
        // The link moves first and the marker is written second. A run that dies between the two leaves
        // the live folder without its finished mark; the link itself still says it is live.
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "CodeSwitchX-0.1.0.11");

        var result = Pwsh.RunCommon(
            $"Write-InstallMarker '{folder}' '0.1.0.11'; " +
            $"New-Item -ItemType Junction -Path '{Path.Combine(temp.Path, "CodeSwitchX")}' -Target '{folder}' | Out-Null; " +
            $"Assert-StableTargetFree '{folder}' '0.1.0.11' '0.1.0.10'");

        result.ExitCode.ShouldBe(1);
        result.Output.ShouldContain("already published");
    }

    [Theory]
    [InlineData("true", 1)]
    [InlineData("false", 0)]
    [InlineData("\"false\"", 0)]
    public void Only_a_real_true_marks_a_folder_finished(string completeJson, int expectedExitCode)
    {
        // A marker edited by hand to "complete": "false" is a non-empty string, which a plain [bool] cast
        // reads as true.
        using var temp = new TempFolder();
        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "CodeSwitchX-0.1.0.11")).FullName;
        File.WriteAllText(
            Path.Combine(folder, "codeswitchx-install.json"),
            $$"""{ "app": "CodeSwitchX", "version": "0.1.0.11", "complete": {{completeJson}} }""");

        var result = Pwsh.RunCommon($"Assert-StableTargetFree '{folder}' '0.1.0.11' '0.1.0.10'");

        result.ExitCode.ShouldBe(expectedExitCode);
    }

    [Theory]
    [InlineData("0.1.0.10", false)]
    [InlineData("0.1.0.10-oneoff.gf31a120", true)]
    [InlineData("0.1.0.10-oneoff.f31a120", true)]
    public void A_one_off_build_only_reuses_a_one_off_folder(string markedVersion, bool reusable)
    {
        // A second line behind the path check: a stable release is never a one-off's folder.
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "CodeSwitchX-test");

        var result = Pwsh.RunCommon(
            $"Write-InstallMarker '{folder}' '{markedVersion}' -Complete; Test-OurInstall '{folder}' -OneOff");

        result.Output.Trim().ShouldBe(reusable.ToString(), StringCompareShould.IgnoreCase);
    }

    [Theory]
    [InlineData(@"alias\CodeSwitchX-0.1.0.11", true)]
    [InlineData(@"alias", true)]
    [InlineData(@"root\CodeSwitchX-0.1.0.11", true)]
    [InlineData(@"rootOld\CodeSwitchX-0.1.0.11", false)]
    public void A_path_through_an_alias_of_the_root_is_still_under_the_root(string path, bool under)
    {
        // E:\STABLE~1\..., a junction or a subst drive leads into E:\StableVersion without matching it as a
        // string. The folder asked about does not exist yet - a new one-off build's target.
        using var temp = new TempFolder();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;

        var result = Pwsh.RunCommon(
            $"New-Item -ItemType Junction -Path '{Path.Combine(temp.Path, "alias")}' -Target '{root}' | Out-Null; " +
            $"Test-PathUnder '{Path.Combine(temp.Path, path)}' '{root}' -OrEqual");

        result.Output.Trim().ShouldBe(under.ToString(), StringCompareShould.IgnoreCase);
    }

    [Fact]
    public void A_clean_that_stops_half_way_leaves_the_marker()
    {
        // The marker is what lets the next run clean the folder. NTFS lists codeswitchx-install.json before
        // CodeSwitchX.exe, so a plain recursive delete takes it first and then stops at the exe somebody
        // holds open - leaving a folder every later run refuses to publish into and refuses to clean.
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "CodeSwitchX-0.1.0.11");

        var result = Pwsh.RunCommon(
            $"Write-InstallMarker '{folder}' '0.1.0.11'; " +
            $"Set-Content '{folder}\\a-free.dll' x; Set-Content '{folder}\\CodeSwitchX.exe' x; " +
            $"$held = [IO.File]::Open('{folder}\\CodeSwitchX.exe', 'Open', 'Read', 'None'); " +
            $"try {{ Remove-InstallFolder '{folder}'; 'cleaned' }} catch {{ 'stopped' }} finally {{ $held.Dispose() }}");

        result.Output.Trim().ShouldBe("stopped");
        File.Exists(Path.Combine(folder, "codeswitchx-install.json")).ShouldBeTrue();
        File.Exists(Path.Combine(folder, "a-free.dll")).ShouldBeFalse();
    }

    [Fact]
    public void A_stable_build_bumps_publishes_and_marks_the_folder_finished()
    {
        using var repo = new FakeRepo("0.1.0.10");

        var result = repo.Build();

        result.ExitCode.ShouldBe(0, result.Output);
        var folder = repo.VersionFolder("0.1.0.11");
        repo.Version.ShouldBe("0.1.0.11");
        FakeRepo.IsMarkedComplete(folder).ShouldBeTrue();
        repo.CurrentLinkTarget.ShouldBe(folder);
        repo.Calls.ShouldContain("shortcut " + repo.CurrentLink);
        repo.Calls.ShouldContain("git push --quiet origin main");
        repo.Calls.ShouldNotContain(call => call.StartsWith("start "));
    }

    [Fact]
    public void A_re_run_after_a_lost_bump_leaves_the_installed_version_alone()
    {
        // The whole chain of the bug: the build is published and made current, the push is rejected, and a
        // reset to origin takes the bump commit away. The next run computes the same number.
        using var repo = new FakeRepo("0.1.0.10");
        var folder = repo.VersionFolder("0.1.0.11");
        var rejected = repo.Build(before: "$env:FAKE_PUSH_FAILS = '1'");
        rejected.Output.ShouldContain("the push was rejected");
        repo.Version = "0.1.0.10";

        var result = repo.Build();

        result.ExitCode.ShouldBe(1);
        result.Output.ShouldContain("0.1.0.11 is already published");
        File.Exists(Path.Combine(folder, "CodeSwitchX.exe")).ShouldBeTrue();
        repo.Version.ShouldBe("0.1.0.10");
    }

    [Fact]
    public void A_build_the_running_version_would_not_close_for_is_rebuilt_by_the_next_run()
    {
        // Published, but the running CodeSwitchX did not close: nothing was made current and the version
        // was not bumped. The script says "run the build again; it rebuilds" - so the folder must not
        // count as finished, or that second run would be refused.
        using var repo = new FakeRepo("0.1.0.10");
        var folder = repo.VersionFolder("0.1.0.11");
        var running = $"$env:FAKE_RUNNING_FROM = '{Path.Combine(repo.CurrentLink, "CodeSwitchX.exe")}'";
        var stuck = repo.Build(before: $"{running}; $env:FAKE_CLOSE_RESULT = 'timeout'");
        stuck.ExitCode.ShouldBe(1);
        stuck.Output.ShouldContain("did not close");
        FakeRepo.IsMarkedComplete(folder).ShouldBeFalse();
        repo.CurrentLinkTarget.ShouldBeNull();

        var result = repo.Build(before: running);

        result.ExitCode.ShouldBe(0, result.Output);
        FakeRepo.IsMarkedComplete(folder).ShouldBeTrue();
        repo.Version.ShouldBe("0.1.0.11");
        repo.Calls.ShouldContain("start " + Path.Combine(repo.CurrentLink, "CodeSwitchX.exe"));
    }

    [Fact]
    public void A_step_that_fails_after_the_close_starts_the_closed_version_again()
    {
        // Once the running version is closed, a junction that cannot be made must not end the script with
        // nobody running. The folder never became current, so the next run rebuilds it.
        using var repo = new FakeRepo("0.1.0.10");
        var folder = repo.VersionFolder("0.1.0.11");
        var oldExe = Path.Combine(repo.VersionFolder("0.1.0.10"), "CodeSwitchX.exe");

        var result = repo.Build(before: $"$env:FAKE_RUNNING_FROM = '{oldExe}'; $env:FAKE_LINK_FAILS = '1'");

        result.ExitCode.ShouldBe(1);
        result.Output.ShouldContain("Could not make 0.1.0.11 the current version - the junction could not be made");
        result.Output.ShouldContain("Run the build again");
        repo.Calls.ShouldContain("close " + oldExe);
        repo.Calls.ShouldContain("start " + oldExe);
        FakeRepo.IsMarkedComplete(folder).ShouldBeFalse();
        repo.Version.ShouldBe("0.1.0.10");

        repo.Build().ExitCode.ShouldBe(0);
        FakeRepo.IsMarkedComplete(folder).ShouldBeTrue();
    }

    [Fact]
    public void A_build_somebody_started_by_hand_before_it_was_current_is_not_cleaned_under_them()
    {
        // The folder is not finished, so the guard would let the next run clean it - and the clean would
        // delete what it can and stop at the locked exe.
        using var repo = new FakeRepo("0.1.0.10");
        var folder = repo.VersionFolder("0.1.0.11");
        var exe = Path.Combine(folder, "CodeSwitchX.exe");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "codeswitchx-install.json"), """{ "app": "CodeSwitchX", "version": "0.1.0.11", "complete": false }""");
        File.WriteAllText(exe, "x");

        var result = repo.Build(before: $"$env:FAKE_RUNNING_FROM = '{exe}'");

        result.ExitCode.ShouldBe(1);
        result.Output.ShouldContain($"CodeSwitchX is running from {folder}");
        File.Exists(exe).ShouldBeTrue();
        repo.Calls.ShouldNotContain(call => call.StartsWith("dotnet publish"));
    }

    [Fact]
    public void A_one_off_build_is_stamped_and_leaves_the_repo_alone()
    {
        using var repo = new FakeRepo("0.1.0.10");
        var folder = Path.Combine(repo.Outside, "CodeSwitchX-test");

        var result = repo.Build($"-InstallDir '{folder}'");

        result.ExitCode.ShouldBe(0, result.Output);
        File.ReadAllText(Path.Combine(folder, "codeswitchx-install.json")).ShouldContain("\"version\": \"0.1.0.10-oneoff.gabc1234\"");
        FakeRepo.IsMarkedComplete(folder).ShouldBeTrue();
        repo.Version.ShouldBe("0.1.0.10");
        repo.Calls.ShouldNotContain(call => call.StartsWith("git commit") || call.StartsWith("git push") || call.StartsWith("shortcut"));
        repo.CurrentLinkTarget.ShouldBeNull();
    }

    [Fact]
    public void A_one_off_build_through_an_alias_of_the_stable_root_is_refused()
    {
        // The folder does not exist yet, so there is no marker to read: only the real path can tell that
        // this one-off would land in the stable root, under the next stable version's name.
        using var repo = new FakeRepo("0.1.0.10");
        var alias = Path.Combine(repo.Outside, "alias");

        var result = repo.Build(
            $"-InstallDir '{Path.Combine(alias, "CodeSwitchX-0.1.0.11")}'",
            before: $"New-Item -ItemType Junction -Path '{alias}' -Target '{repo.Root}' | Out-Null");

        result.ExitCode.ShouldBe(1);
        result.Output.ShouldContain("must be outside the stable root");
        Directory.Exists(repo.VersionFolder("0.1.0.11")).ShouldBeFalse();
    }

    [Fact]
    public void A_one_off_build_never_cleans_a_stable_folder()
    {
        using var repo = new FakeRepo("0.1.0.10");
        var folder = Directory.CreateDirectory(Path.Combine(repo.Outside, "looks-like-a-test-folder")).FullName;
        File.WriteAllText(Path.Combine(folder, "codeswitchx-install.json"), """{ "app": "CodeSwitchX", "version": "0.1.0.10", "complete": true }""");
        File.WriteAllText(Path.Combine(folder, "CodeSwitchX.exe"), "x");

        var result = repo.Build($"-InstallDir '{folder}' -Clean");

        result.ExitCode.ShouldBe(1);
        result.Output.ShouldContain("not a one-off CodeSwitchX build");
        File.Exists(Path.Combine(folder, "CodeSwitchX.exe")).ShouldBeTrue();
    }

    [Fact]
    public void A_good_build_does_not_wait_for_a_key_unless_Explorer_started_it()
    {
        // PowerShell, Git Bash, a VS Code task and a CI step all run a .cmd as cmd /c "<path>", exactly as
        // a double click does. Their window stays open, or nobody is there - and a caller that leaves stdin
        // open would wait for ever. This test process is such a caller. -? is a quick success.
        var result = Cmd.Run($"/c \"\"{BuildCmd}\" -?\"");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldNotContain(PauseLine);
    }

    [Fact]
    public void A_failed_build_waits_for_a_key()
    {
        // -Part takes four values; anything else stops PowerShell before build.ps1 runs a line.
        var result = Cmd.Run($"/c \"\"{BuildCmd}\" -Part nonsense\"");

        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain(PauseLine);
    }

    [Theory]
    [InlineData("always", "-?", true)]
    [InlineData("never", "-Part nonsense", false)]
    public void The_wait_can_be_forced_or_switched_off(string setting, string arguments, bool waits)
    {
        // "always" is what a double click amounts to; "never" is for a caller that must not hang.
        var result = Cmd.Run($"/c \"\"{BuildCmd}\" {arguments}\"", ("CODESWITCHX_BUILD_PAUSE", setting));

        result.Output.Contains(PauseLine).ShouldBe(waits);
    }

    private static string BuildCmd => Path.Combine(Pwsh.ScriptsFolder, "build.cmd");

    private sealed record ScriptResult(int ExitCode, string Output);

    /// <summary>
    /// A throwaway copy of the scripts in a repo-shaped folder, with a stable root of its own. The copy of
    /// _common.ps1 ends with fakes for everything that reaches outside that folder: git, the SDK, the Start
    /// Menu shortcut, and finding, closing and starting CodeSwitchX. build.ps1 is the real file.
    /// </summary>
    private sealed class FakeRepo : IDisposable
    {
        private readonly TempFolder _temp = new();

        public FakeRepo(string version)
        {
            Root = Directory.CreateDirectory(Path.Combine(_temp.Path, "stable")).FullName;
            Outside = Directory.CreateDirectory(Path.Combine(_temp.Path, "builds")).FullName;
            var scripts = Directory.CreateDirectory(Path.Combine(Repo, "scripts")).FullName;
            File.Copy(Path.Combine(Pwsh.ScriptsFolder, "build.ps1"), Path.Combine(scripts, "build.ps1"));
            File.WriteAllText(
                Path.Combine(scripts, "_common.ps1"),
                File.ReadAllText(Path.Combine(Pwsh.ScriptsFolder, "_common.ps1")) + Fakes);
            var project = Directory.CreateDirectory(Path.Combine(Repo, "src", "CodeSwitchX.UI")).FullName;
            File.WriteAllText(Path.Combine(project, "CodeSwitchX.UI.csproj"), "<Project />");
            Version = version;
        }

        private string Repo => Path.Combine(_temp.Path, "repo");

        private string Log => Path.Combine(_temp.Path, "calls.log");

        private string Props => Path.Combine(Repo, "Directory.Build.props");

        /// <summary>The stable root: what E:\StableVersion is on the real machine.</summary>
        public string Root { get; }

        /// <summary>A folder outside both the repo and the stable root, for one-off builds.</summary>
        public string Outside { get; }

        public string CurrentLink => Path.Combine(Root, "CodeSwitchX");

        public string? CurrentLinkTarget => new DirectoryInfo(CurrentLink).LinkTarget;

        public string[] Calls => File.Exists(Log) ? File.ReadAllLines(Log) : [];

        public string Version
        {
            get => Regex.Match(File.ReadAllText(Props), "<Version>([^<]+)</Version>").Groups[1].Value;
            set => File.WriteAllText(Props, $"<Project>\r\n  <PropertyGroup>\r\n    <Version>{value}</Version>\r\n  </PropertyGroup>\r\n</Project>\r\n");
        }

        public string VersionFolder(string version) => Path.Combine(Root, $"CodeSwitchX-{version}");

        public static bool IsMarkedComplete(string folder) =>
            Regex.IsMatch(File.ReadAllText(Path.Combine(folder, "codeswitchx-install.json")), "\"complete\":\\s*true");

        /// <summary>Runs the real build.ps1 against this repo. <paramref name="before"/> sets the fakes' switches.</summary>
        public ScriptResult Build(string arguments = "", string before = "") =>
            Pwsh.Run($"{before}\n& '{Path.Combine(Repo, "scripts", "build.ps1")}' {arguments}");

        public void Dispose() => _temp.Dispose();

        private string Fakes => $$"""


            # --- test fakes: nothing below reaches outside the test's own folder ---
            $script:DefaultInstallRoot = '{{Root}}'
            function Write-FakeCall { param([string]$Line) Add-Content -LiteralPath '{{Log}}' -Value $Line }
            function git {
                $global:LASTEXITCODE = 0
                $rest = @($args | Select-Object -Skip 2)
                Write-FakeCall "git $($rest -join ' ')"
                switch ($rest[0]) {
                    'rev-parse' { if ($rest -contains '--short') { 'abc1234' } else { 'main' } }
                    'rev-list'  { "0`t0" }
                    'push'      { if ($env:FAKE_PUSH_FAILS) { $global:LASTEXITCODE = 1 } }
                }
            }
            function dotnet {
                $global:LASTEXITCODE = 0
                if ($args[0] -eq '--version') { return '10.0.0-fake' }
                Write-FakeCall "dotnet $($args[0]) $(Split-Path -Leaf $args[1])"
                $out = $args[[array]::IndexOf($args, '--output') + 1]
                foreach ($file in $AppExeName, $RelayRelativePath) {
                    New-Item -ItemType File -Path (Join-Path $out $file) -Force | Out-Null
                }
            }
            function Write-StartMenuShortcut { param([string]$InstallDir) Write-FakeCall "shortcut $InstallDir"; return 'fake.lnk' }
            function Get-AppProcess {
                param([string]$InstallDir, [switch]$AnyVersion, [string]$InstallRoot)
                if (-not $env:FAKE_RUNNING_FROM) { return @() }
                if (-not $AnyVersion -and $env:FAKE_RUNNING_FROM -ine (Join-Path (Resolve-FullPath $InstallDir) $AppExeName)) { return @() }
                return @([pscustomobject]@{ ProcessId = 4242; ExecutablePath = $env:FAKE_RUNNING_FROM })
            }
            function Get-OtherAppProcess { return @() }
            function Stop-AppProcess {
                param($Process)
                Write-FakeCall "close $($Process.ExecutablePath)"
                if ($env:FAKE_CLOSE_RESULT) { return $env:FAKE_CLOSE_RESULT }
                return 'closed'
            }
            function Start-App { param([string]$ExePath) Write-FakeCall "start $ExePath"; return [pscustomobject]@{ Id = 4243 } }
            if ($env:FAKE_LINK_FAILS) { function Set-CurrentLink { throw 'the junction could not be made' } }

            """;
    }

    private static class Cmd
    {
        public static ScriptResult Run(string arguments, params (string Name, string Value)[] environment) =>
            Shell.Run("cmd.exe", arguments, stdin: "", environment);
    }

    private static class Pwsh
    {
        public static string ScriptsFolder { get; } = Path.Combine(FindRepoRoot(), "scripts");

        /// <summary>Runs <paramref name="command"/> after dot-sourcing _common.ps1, as build.ps1 does.</summary>
        public static ScriptResult RunCommon(string command) =>
            Run($". '{Path.Combine(ScriptsFolder, "_common.ps1")}'\n{command}");

        public static ScriptResult Run(string script) =>
            Shell.Run("pwsh", "-NoProfile -NonInteractive -Command -", script + "\nexit $LASTEXITCODE\n");

        /// <summary>
        /// The test output can be anywhere - -p:BaseOutputPath is how a build dodges a DLL the running app
        /// holds - so when it is not under the repo, this source file's own path is asked instead.
        /// </summary>
        private static string FindRepoRoot([CallerFilePath] string thisFile = "")
        {
            foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile) })
            {
                for (var directory = start is null ? null : new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                {
                    if (File.Exists(Path.Combine(directory.FullName, "CodeSwitchX.slnx")))
                    {
                        return directory.FullName;
                    }
                }
            }

            throw new InvalidOperationException("CodeSwitchX.slnx not found above the test output or above this source file.");
        }
    }

    private static class Shell
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

        public static ScriptResult Run(string fileName, string arguments, string stdin, params (string Name, string Value)[] environment)
        {
            var start = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var (name, value) in environment)
            {
                start.Environment[name] = value;
            }

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();

            // A script that waits - a pause reading a console, a real git asking for a login - would
            // otherwise hang the whole test run with nothing to say which test it was.
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(10));
                var soFar = stdout.IsCompleted && stderr.IsCompleted ? stdout.Result + stderr.Result : "(not readable)";
                throw new TimeoutException($"{fileName} did not finish within {Timeout.TotalSeconds:0} s and was killed. Output so far:\n{soFar}");
            }

            return new ScriptResult(process.ExitCode, stdout.Result + stderr.Result);
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
                RemoveLinks(new DirectoryInfo(Path));
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup: a lingering handle must not fail the suite.
            }
        }

        /// <summary>A recursive delete refuses a junction, so each one is taken out first - the link, never its target.</summary>
        private static void RemoveLinks(DirectoryInfo folder)
        {
            foreach (var child in folder.EnumerateDirectories())
            {
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    child.Delete();
                }
                else
                {
                    RemoveLinks(child);
                }
            }
        }
    }
}
