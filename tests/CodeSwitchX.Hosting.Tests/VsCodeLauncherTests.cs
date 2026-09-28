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
    public void Launch_without_an_executable_reports_an_error_instead_of_throwing()
    {
        var launcher = new VsCodeLauncher(executable: @"C:\definitely\missing\Code.exe");

        var result = launcher.Launch(new Workspace { Name = "App", RootPath = AppContext.BaseDirectory });

        result.Started.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void A_folder_that_no_longer_exists_is_reported_instead_of_being_opened_as_a_new_file()
    {
        // VS Code opens a missing command-line path as a new file whose tab, and so the title, carries the folder's
        // name: the window would be adopted as the workspace, and saving it would write a file where the folder was.
        var launcher = new VsCodeLauncher(executable: typeof(VsCodeLauncher).Assembly.Location);
        var missing = Path.Combine(Path.GetTempPath(), "codeswitchx-missing-" + Guid.NewGuid().ToString("N"));

        var folder = launcher.Launch(new Workspace { Name = "App", RootPath = missing });
        var file = launcher.Launch(new Workspace { Name = "App", RootPath = AppContext.BaseDirectory, WorkspaceFile = missing + ".code-workspace" });

        folder.Started.ShouldBeFalse();
        folder.Error.ShouldNotBeNull().ShouldContain(missing);
        file.Started.ShouldBeFalse();
        file.Error.ShouldNotBeNull().ShouldContain(missing + ".code-workspace");
    }
}
