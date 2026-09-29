using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting.VsCode;

namespace CodeSwitchX.Hosting.Tests;

public class VsCodeLauncherTests
{
    [Fact]
    public void Folder_workspaces_open_the_root_in_a_new_window()
    {
        var workspace = new Workspace { Name = "App", RootPath = @"c:\repo\app" };

        VsCodeLauncher.BuildArguments(workspace).ShouldBe(["--new-window", @"c:\repo\app"]);
        VsCodeLauncher.DisplayNameForMatching(workspace).ShouldBe("app");
    }

    [Fact]
    public void Workspace_files_and_profiles_are_passed_through()
    {
        var workspace = new Workspace { Name = "App", RootPath = @"c:\repo\app", WorkspaceFile = @"c:\repo\app\app.code-workspace", VsCodeProfile = "Dev Kit" };

        VsCodeLauncher.BuildArguments(workspace).ShouldBe(["--new-window", "--profile", "Dev Kit", @"c:\repo\app\app.code-workspace"]);
        VsCodeLauncher.DisplayNameForMatching(workspace).ShouldBe("app (Workspace)");
    }

    [Fact]
    public void A_drive_root_reaches_vs_code_whole_and_is_matched_by_its_drive()
    {
        // PathNormalizer keeps the separator on a drive root (a subst drive for a long repo path, say). Quoted by hand,
        // the backslash before the closing quote escaped it, so VS Code was asked to open R:" instead. Each argument now
        // goes to ProcessStartInfo.ArgumentList as it is, which quotes it for Windows' parser.
        var workspace = new Workspace { Name = "Repo", RootPath = @"R:\", VsCodeProfile = "My \"Dev\" Kit" };

        VsCodeLauncher.BuildArguments(workspace).ShouldBe(["--new-window", "--profile", "My \"Dev\" Kit", @"R:\"]);
        VsCodeLauncher.DisplayNameForMatching(workspace).ShouldBe("R:", "VS Code names a drive root by its drive: the base name of /R:/");
    }

    [Fact]
    public void VS_Code_starts_without_the_electron_variable_that_would_run_it_as_plain_node()
    {
        // A VS Code terminal or extension host (a Claude Code session) sets ELECTRON_RUN_AS_NODE=1. A CodeSwitchX started
        // from there passed it on, and every Code.exe it opened ran as Node and exited with code 9, without a window.
        var before = Environment.GetEnvironmentVariable(ElectronRunAsNode);
        Environment.SetEnvironmentVariable(ElectronRunAsNode, "1");
        try
        {
            var info = VsCodeLauncher.BuildStartInfo(@"C:\VS Code\Code.exe", new Workspace { Name = "App", RootPath = @"c:\repo\app" });

            info.Environment.ShouldNotContainKey(ElectronRunAsNode);
            info.Environment.ShouldContainKey("PATH", "only the one variable goes; VS Code needs the rest of the environment");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ElectronRunAsNode, before);
        }
    }

    private const string ElectronRunAsNode = "ELECTRON_RUN_AS_NODE";

    [Fact]
    public void Launch_without_an_executable_reports_an_error_instead_of_throwing()
    {
        var launcher = new VsCodeLauncher(() => @"C:\definitely\missing\Code.exe");

        var result = launcher.Launch(new Workspace { Name = "App", RootPath = AppContext.BaseDirectory });

        result.Started.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void A_folder_that_no_longer_exists_is_reported_instead_of_being_opened_as_a_new_file()
    {
        // VS Code opens a missing command-line path as a new file whose tab, and so the title, carries the folder's
        // name: the window would be adopted as the workspace, and saving it would write a file where the folder was.
        var launcher = new VsCodeLauncher(() => typeof(VsCodeLauncher).Assembly.Location);
        var missing = Path.Combine(Path.GetTempPath(), "codeswitchx-missing-" + Guid.NewGuid().ToString("N"));

        var folder = launcher.Launch(new Workspace { Name = "App", RootPath = missing });
        var file = launcher.Launch(new Workspace { Name = "App", RootPath = AppContext.BaseDirectory, WorkspaceFile = missing + ".code-workspace" });

        folder.Started.ShouldBeFalse();
        folder.Error.ShouldNotBeNull().ShouldContain(missing);
        file.Started.ShouldBeFalse();
        file.Error.ShouldNotBeNull().ShouldContain(missing + ".code-workspace");
    }

    [Fact]
    public void An_override_that_is_not_Code_exe_is_refused_with_the_reason_instead_of_a_timeout_at_every_open()
    {
        // Insiders ("Code - Insiders.exe") or VSCodium: the launch works, but windows are matched by the process name Code and
        // a title that says Visual Studio Code, so every open timed out after 20 s without a word about why.
        var dir = Directory.CreateTempSubdirectory("csx-launcher-");
        try
        {
            var insiders = Path.Combine(dir.FullName, "Code - Insiders.exe");
            File.WriteAllBytes(insiders, []);
            var launcher = new VsCodeLauncher(() => insiders);

            var result = launcher.Launch(new Workspace { Name = "App", RootPath = AppContext.BaseDirectory });

            result.Started.ShouldBeFalse();
            result.Error.ShouldNotBeNull().ShouldContain("Code.exe");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void VS_Code_installed_after_the_start_is_found_at_the_next_open()
    {
        // The executable was located once, when the singleton was built: VS Code installed while CodeSwitchX ran was "not found" until a restart.
        var dir = Directory.CreateTempSubdirectory("csx-launcher-");
        try
        {
            var code = Path.Combine(dir.FullName, "Code.exe");
            var launcher = new VsCodeLauncher(locate: () => File.Exists(code) ? code : null);
            var workspace = new Workspace { Name = "App", RootPath = AppContext.BaseDirectory };

            launcher.Launch(workspace).Error.ShouldNotBeNull().ShouldContain(VsCodeLocator.OverrideVariable, Case.Sensitive, "not found before the install");
            File.WriteAllBytes(code, []);

            var result = launcher.Launch(workspace);

            result.Started.ShouldBeFalse("an empty file is no program; what matters is that it was found and started");
            result.Error.ShouldNotBeNull().ShouldNotContain(VsCodeLocator.OverrideVariable);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
