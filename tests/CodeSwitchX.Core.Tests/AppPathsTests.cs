using CodeSwitchX.Core;

namespace CodeSwitchX.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void Files_live_under_the_root()
    {
        var paths = new AppPaths(@"C:\data\csx");

        paths.DatabaseFile.ShouldBe(@"C:\data\csx\codeswitchx.db");
        paths.TokenFile.ShouldBe(@"C:\data\csx\token");
        paths.EndpointFile.ShouldBe(@"C:\data\csx\endpoint.json");
        paths.LogsDirectory.ShouldBe(@"C:\data\csx\logs");
    }

    [Fact]
    public void Default_root_is_LocalAppData_CodeSwitchX()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeSwitchX");

        AppPaths.Default().Root.ShouldBe(expected);
    }

    [Fact]
    public void EnsureCreated_creates_root_and_logs()
    {
        var root = Path.Combine(Path.GetTempPath(), "csx-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            new AppPaths(root).EnsureCreated();
            Directory.Exists(root).ShouldBeTrue();
            Directory.Exists(Path.Combine(root, "logs")).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ClaudeCodePaths_point_into_the_home_folder()
    {
        var claude = new ClaudeCodePaths(@"C:\Users\me");

        claude.SettingsFile.ShouldBe(@"C:\Users\me\.claude\settings.json");
        claude.ProjectsDirectory.ShouldBe(@"C:\Users\me\.claude\projects");
    }

    [Fact]
    public void CLAUDE_CONFIG_DIR_moves_the_claude_folder_the_way_claude_code_does()
    {
        var claude = ClaudeCodePaths.Resolve(@"C:\Users\me", @"D:\claude");

        claude.SettingsFile.ShouldBe(@"D:\claude\settings.json");
        claude.ProjectsDirectory.ShouldBe(@"D:\claude\projects");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void An_unset_CLAUDE_CONFIG_DIR_keeps_the_home_folder(string? configDirectory)
    {
        ClaudeCodePaths.Resolve(@"C:\Users\me", configDirectory).SettingsFile.ShouldBe(@"C:\Users\me\.claude\settings.json");
    }
}
