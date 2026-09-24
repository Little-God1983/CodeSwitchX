using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting.VsCode;

namespace CodeSwitchX.Hosting.Tests;

public class VsCodeLauncherTests
{
    [Fact]
    public void Folder_workspaces_open_the_root_in_a_new_window()
    {
        var workspace = new Workspace { Name = "App", RootPath = @"c:\repo\app" };

        VsCodeLauncher.BuildArguments(workspace).ShouldBe("--new-window \"c:\\repo\\app\"");
        VsCodeLauncher.DisplayNameForMatching(workspace).ShouldBe("app");
    }

    [Fact]
    public void Workspace_files_and_profiles_are_passed_through()
    {
        var workspace = new Workspace { Name = "App", RootPath = @"c:\repo\app", WorkspaceFile = @"c:\repo\app\app.code-workspace", VsCodeProfile = "Dev Kit" };

        VsCodeLauncher.BuildArguments(workspace).ShouldBe("--new-window --profile \"Dev Kit\" \"c:\\repo\\app\\app.code-workspace\"");
        VsCodeLauncher.DisplayNameForMatching(workspace).ShouldBe("app (Workspace)");
    }

    [Fact]
    public void Launch_without_an_executable_reports_an_error_instead_of_throwing()
    {
        var launcher = new VsCodeLauncher(executable: @"C:\definitely\missing\Code.exe");

        var result = launcher.Launch(new Workspace { Name = "App", RootPath = @"c:\repo\app" });

        result.Started.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }
}
