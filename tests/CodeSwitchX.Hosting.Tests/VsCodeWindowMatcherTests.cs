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

    [Fact]
    public void A_window_vscode_has_not_shown_yet_is_no_new_match_but_a_hidden_existing_one_is_adopted()
    {
        var hidden = new WindowInfo(800, 30, "Chrome_WidgetWin_1", "App - Visual Studio Code") { IsVisible = false };

        VsCodeWindowMatcher.FindNew([], [hidden], "App", ProcessName).ShouldBeNull("adopting it before VS Code shows it would race its own ShowWindow and flash it undocked");
        VsCodeWindowMatcher.FindAllExisting([hidden], "App", ProcessName).Single().Hwnd.ShouldBe((nint)800);
    }

    [Fact]
    public void A_name_that_contains_the_title_separator_matches_its_own_window()
    {
        // "app - Copy" is the name Explorer gives a copied folder; VS Code writes it into the title unchanged.
        var window = new WindowInfo(500, 30, "Chrome_WidgetWin_1", "● Program.cs - Kunde - Portal - Visual Studio Code");

        VsCodeWindowMatcher.FindNew([], [window], "Kunde - Portal", ProcessName)!.Hwnd.ShouldBe((nint)500);
        VsCodeWindowMatcher.FindAllExisting([window], "kunde - portal", ProcessName).Single().Hwnd.ShouldBe((nint)500);
        VsCodeWindowMatcher.FindNew([], [window], "Kunde - Portal2", ProcessName).ShouldBeNull();
        VsCodeWindowMatcher.FindNew([], [window], "Portal - Kunde", ProcessName).ShouldBeNull();
    }

    [Theory]
    [InlineData("Program.cs - app - Visual Studio Code", "app", null, true)]
    [InlineData("● app - Visual Studio Code", "app", null, true)]
    [InlineData("app - Visual Studio Code [Administrator]", "app", null, true)]
    [InlineData("Program.cs - App - Copy - Visual Studio Code", "App - Copy", null, true)]
    [InlineData("Program.cs - app - Dev Kit - Visual Studio Code", "app", "Dev Kit", true)]
    [InlineData("Program.cs - app - Dev Kit - Visual Studio Code", "app", null, false)]
    [InlineData("Program.cs - App - Copy - Visual Studio Code", "App", null, false)]
    [InlineData("Dockerfile - proj - Visual Studio Code", "Dockerfile", null, false)]
    [InlineData("Program.cs - proj - Python - Visual Studio Code", "Python", null, false)]
    [InlineData("app", "app", null, false)]
    public void Only_the_place_where_vs_code_writes_the_folder_name_is_sure_to_be_the_folder(string title, string name, string? profile, bool sure)
    {
        // "${activeEditorShort} - ${rootName} - ${profileName} - ${appName}", each part only when there is one. Another
        // segment can be an editor tab, a profile or part of a longer folder name, so a match there is only a guess.
        VsCodeWindowMatcher.TitleNamesRoot(title, name, profile).ShouldBe(sure);
    }
}
