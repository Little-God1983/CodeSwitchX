using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.Hosting.Tests;

public class VsCodeWindowMatcherTests
{
    private static readonly Dictionary<uint, string> Processes = new() { [10] = "Code", [20] = "chrome", [30] = "Code" };
    private static string? ProcessName(uint pid) => Processes.GetValueOrDefault(pid);

    private static readonly WindowInfo Existing = new(100, 10, "Chrome_WidgetWin_1", "App - Visual Studio Code");

    [Fact]
    public void Picks_the_new_code_window_whose_title_names_the_workspace()
    {
        var after = new List<WindowInfo>
        {
            Existing,
            new(200, 20, "Chrome_WidgetWin_1", "App - Google Chrome"),
            new(300, 30, "Chrome_WidgetWin_1", string.Empty),
            new(400, 30, "Chrome_WidgetWin_1", "Program.cs - App2 - Visual Studio Code"),
            new(500, 30, "Chrome_WidgetWin_1", "Program.cs - App - Visual Studio Code"),
        };

        VsCodeWindowMatcher.FindNew([Existing], after, "App", ProcessName)!.Hwnd.ShouldBe((nint)500);
        VsCodeWindowMatcher.FindNew([Existing], after, "App2", ProcessName)!.Hwnd.ShouldBe((nint)400);
    }

    [Fact]
    public void Ignores_windows_that_existed_before_launch_and_other_processes()
    {
        var after = new List<WindowInfo> { Existing, new(200, 20, "Chrome_WidgetWin_1", "App - Visual Studio Code") };

        VsCodeWindowMatcher.FindNew([Existing], after, "App", ProcessName).ShouldBeNull();
    }

    [Fact]
    public void Matching_is_case_insensitive_and_prefers_titles_that_say_visual_studio_code()
    {
        var after = new List<WindowInfo>
        {
            new(600, 30, "Chrome_WidgetWin_1", "app"),
            new(700, 30, "Chrome_WidgetWin_1", "APP - Visual Studio Code"),
        };

        VsCodeWindowMatcher.FindNew([], after, "App", ProcessName)!.Hwnd.ShouldBe((nint)700);
    }
}
