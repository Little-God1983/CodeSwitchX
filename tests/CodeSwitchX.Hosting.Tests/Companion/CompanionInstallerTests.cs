using System.IO.Compression;
using System.Text.Json;
using CodeSwitchX.Hosting.VsCode.Companion;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.Hosting.Tests.Companion;

public sealed class CompanionInstallerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "csx-companion-install-" + Guid.NewGuid().ToString("N"));
    private readonly List<string[]> _calls = [];
    private readonly Dictionary<string, string> _listed = new(StringComparer.Ordinal);
    private int _installExit;
    private readonly CompanionInstaller _installer;

    public CompanionInstallerTests()
    {
        Directory.CreateDirectory(_directory);
        _installer = new CompanionInstaller(CompanionPackage.Shipped, _directory, () => @"C:\VS Code\Code.exe", (arguments, _) =>
        {
            _calls.Add([.. arguments]);
            var profile = arguments.SkipWhile(a => a != "--profile").Skip(1).FirstOrDefault() ?? "";
            return Task.FromResult(arguments[0] == "--list-extensions"
                ? (0, _listed.GetValueOrDefault(profile, "ms-python.python@2026.1.0\n"))
                : (_installExit, _installExit == 0 ? "Extension 'x.vsix' was successfully installed." : "Error: profile not found"));
        }, NullLogger<CompanionInstaller>.Instance);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Vsix => Path.Combine(_directory, $"codeswitchx-companion-{CompanionPackage.Shipped.Version}.vsix");

    [Fact]
    public async Task A_profile_without_it_gets_it_installed()
    {
        var result = await _installer.EnsureAsync([null, "Work"], Ct);

        result.Installed.ShouldBe(["default", "Work"]);
        result.Failed.ShouldBeEmpty();
        _calls.ShouldBe([
            ["--list-extensions", "--show-versions"],
            ["--install-extension", Vsix, "--force"],
            ["--list-extensions", "--show-versions", "--profile", "Work"],
            ["--install-extension", Vsix, "--force", "--profile", "Work"],
        ]);
        File.Exists(Vsix).ShouldBeTrue();
    }

    [Fact]
    public async Task A_profile_that_has_this_version_is_left_alone_and_another_version_is_replaced()
    {
        _listed[""] = $"codeswitchx.companion@{CompanionPackage.Shipped.Version}\nms-python.python@2026.1.0\n";
        _listed["Work"] = "codeswitchx.companion@0.0.1\n";

        var result = await _installer.EnsureAsync([null, "Work", "work", " "], Ct);

        result.Installed.ShouldBe(["Work"]);
        _calls.Count(c => c[0] == "--install-extension").ShouldBe(1);
        _calls.Count(c => c[0] == "--list-extensions").ShouldBe(2, "a profile named twice, and a blank name for the default, are looked at once");
    }

    [Fact]
    public async Task A_failed_install_is_said_with_the_CLI_s_last_line()
    {
        _installExit = 1;

        var result = await _installer.EnsureAsync(["Gone"], Ct);

        result.Installed.ShouldBeEmpty();
        result.Failed.ShouldBe(["Gone: Error: profile not found"]);
    }

    [Fact]
    public async Task A_CLI_that_cannot_run_is_a_failure_not_a_throw()
    {
        var installer = new CompanionInstaller(CompanionPackage.Shipped, _directory, () => null,
            (_, _) => throw new InvalidOperationException("VS Code was not found."), NullLogger<CompanionInstaller>.Instance);

        (await installer.EnsureAsync([null], Ct)).Failed.ShouldBe(["default: VS Code was not found."]);
    }

    [Theory]
    [InlineData("codeswitchx.companion@0.1.0\r\n", "0.1.0")]
    [InlineData("a.b@1.0.0\nCodeSwitchX.Companion@0.2.0\n", "0.2.0")]
    [InlineData("codeswitchx.companionx@0.1.0\n", null)]
    [InlineData("", null)]
    public void The_installed_version_is_read_from_the_list(string output, string? version)
    {
        CompanionInstaller.InstalledVersion(output).ShouldBe(version);
    }

    [Fact]
    public void The_CLI_script_is_the_one_code_cmd_names_in_its_build_folder()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_directory, "VS Code", "bin")).FullName;
        var script = Path.Combine(_directory, "VS Code", "07f806f999", "resources", "app", "out", "cli.js");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, "");
        File.WriteAllText(Path.Combine(bin, "code.cmd"),
            "@echo off\r\nsetlocal\r\nset ELECTRON_RUN_AS_NODE=1\r\n\"%~dp0..\\Code.exe\" \"%~dp0..\\07f806f999\\resources\\app\\out\\cli.js\" %*\r\nendlocal\r\n");

        CompanionInstaller.CliScript(Path.Combine(_directory, "VS Code", "Code.exe")).ShouldBe(script);
    }

    [Fact]
    public void The_CLI_script_of_the_old_layout_is_found_without_a_shim()
    {
        var script = Path.Combine(_directory, "Old", "resources", "app", "out", "cli.js");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, "");

        CompanionInstaller.CliScript(Path.Combine(_directory, "Old", "Code.exe")).ShouldBe(script);
        CompanionInstaller.CliScript(Path.Combine(_directory, "Nothing", "Code.exe")).ShouldBeNull();
    }

    [Fact]
    public void The_vsix_holds_the_manifest_and_the_extension_s_files()
    {
        CompanionPackage.Shipped.WriteVsix(Vsix);

        using var zip = ZipFile.OpenRead(Vsix);
        zip.Entries.Select(e => e.FullName).ShouldBe(["[Content_Types].xml", "extension.vsixmanifest", "extension/package.json", "extension/extension.js"]);
        using var package = JsonDocument.Parse(zip.GetEntry("extension/package.json")!.Open());
        package.RootElement.GetProperty("publisher").GetString().ShouldBe("codeswitchx");
        package.RootElement.GetProperty("name").GetString().ShouldBe("companion");
        var manifest = new StreamReader(zip.GetEntry("extension.vsixmanifest")!.Open()).ReadToEnd();
        manifest.ShouldContain($"Id=\"companion\" Version=\"{CompanionPackage.Shipped.Version}\" Publisher=\"codeswitchx\"");
        manifest.ShouldContain("Value=\"^1.90.0\"");
        new StreamReader(zip.GetEntry("extension/extension.js")!.Open()).ReadToEnd().ShouldContain("claude-vscode.editor.open");
    }
}
