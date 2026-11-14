using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Yard;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>#423: a repository with the stable-build scripts, and whether its line lets a build start.</summary>
public class StableBuildTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csx-stable-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>A repository (a .git folder at its top) whose scripts folder holds the given files, each with the given text.</summary>
    private string Repo(params (string Name, string Text)[] scripts)
    {
        var dir = Path.Combine(_root, "scripts");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        foreach (var (name, text) in scripts)
        {
            File.WriteAllText(Path.Combine(dir, name), text);
        }

        return _root;
    }

    private const string Common = "$script:DefaultInstallRoot = 'E:\\StableVersion'\r\n$script:ReleaseBranch = 'main'\r\n$script:ShortcutName       = 'Some App.lnk'\r\n";

    private static GitLine Line(string? branch, int? dirty, string? release = null) =>
        new(null, branch, dirty, StableBuild: new StableBuildRepo(@"c:\repo\app", "App.lnk", release));

    [Fact]
    public void Find_reads_the_shortcut_name_and_the_release_branch_from_common()
    {
        var repo = Repo(("build.ps1", "# build"), ("_common.ps1", Common));

        var found = StableBuildRepo.Find(repo).ShouldNotBeNull();

        found.ShortcutName.ShouldBe("Some App.lnk");
        found.ReleaseBranch.ShouldBe("main");
        found.RepoPath.ShouldBe(repo);
        found.BuildScript.ShouldBe(Path.Combine(repo, "scripts", "build.ps1"));
    }

    [Fact]
    public void Find_accepts_the_name_in_double_quotes_and_any_casing_of_script()
    {
        var repo = Repo(("build.ps1", "# build"), ("_common.ps1", "  $Script:ShortcutName = \"X.lnk\"\n"));

        var found = StableBuildRepo.Find(repo).ShouldNotBeNull();

        found.ShortcutName.ShouldBe("X.lnk");
        found.ReleaseBranch.ShouldBeNull("no line names it");
    }

    [Fact]
    public void Find_takes_an_apostrophe_inside_double_quotes()
    {
        var repo = Repo(("build.ps1", "# build"), ("_common.ps1", "$script:ShortcutName = \"Bob's App.lnk\"\n"));

        StableBuildRepo.Find(repo).ShouldNotBeNull().ShortcutName.ShouldBe("Bob's App.lnk");
    }

    [Fact]
    public void Find_does_not_take_a_name_powershell_would_fill_in_from_a_variable()
    {
        var repo = Repo(("build.ps1", "# build"), ("_common.ps1", "$script:ShortcutName = \"$AppName.lnk\"\n"));

        StableBuildRepo.Find(repo).ShouldBeNull("the file's text is not the name PowerShell makes of it");
    }

    [Fact]
    public void A_release_branch_powershell_would_fill_in_is_not_taken()
    {
        var repo = Repo(("build.ps1", "# build"), ("_common.ps1", "$script:ReleaseBranch = \"$branch\"\n$script:ShortcutName = 'X.lnk'\n"));

        StableBuildRepo.Find(repo).ShouldNotBeNull().ReleaseBranch.ShouldBeNull("main or develop then, not a branch that cannot exist");
    }

    [Fact]
    public void A_semicolon_in_the_path_is_escaped_for_windows_terminal()
    {
        // wt takes a bare ; as the start of its next command.
        var arguments = StableBuildLauncher.TerminalArguments(new StableBuildRepo(@"E:\Repos\a;b", "X.lnk"), "pwsh.exe");

        arguments.ShouldBe(@"-w new -d ""E:\Repos\a\;b"" pwsh.exe -NoProfile -ExecutionPolicy Bypass -NoExit -File ""E:\Repos\a\;b\scripts\build.ps1""");
    }

    [Fact]
    public void A_drive_root_goes_to_windows_terminal_without_a_separator_before_the_quote()
    {
        StableBuildLauncher.TerminalArguments(new StableBuildRepo(@"E:\", "X.lnk"), "pwsh.exe").ShouldContain(@"-d ""E:\."" pwsh.exe");
    }

    [Fact]
    public void The_build_always_gets_a_terminal_window_of_its_own()
    {
        // Not a tab in the window the user types in, whatever Windows Terminal is set to do with new tabs.
        StableBuildLauncher.TerminalArguments(new StableBuildRepo(@"E:\Repos\App", "X.lnk"), "pwsh.exe").ShouldStartWith("-w new ");
    }

    [Fact]
    public void Find_takes_a_doubled_quote_inside_single_quotes()
    {
        var repo = Repo(("build.ps1", "# build"), ("_common.ps1", "$script:ShortcutName = 'Bob''s App.lnk'\n"));

        StableBuildRepo.Find(repo).ShouldNotBeNull().ShortcutName.ShouldBe("Bob's App.lnk");
    }

    [Fact]
    public void Find_takes_the_last_assignment_as_powershell_does()
    {
        var repo = Repo(("build.ps1", "# build"), ("_common.ps1", "$script:ShortcutName = 'Old.lnk'\n$script:ShortcutName = 'New.lnk'\n"));

        StableBuildRepo.Find(repo).ShouldNotBeNull().ShortcutName.ShouldBe("New.lnk");
    }

    [Fact]
    public void The_release_branch_is_matched_in_any_case_as_the_script_does()
    {
        StableBuildCheck.BuildBlock(Line("main", 0, "Main")).ShouldBeNull("the script compares with -ne, which ignores case");
    }

    [Fact]
    public void An_assignment_in_a_block_comment_is_no_setting()
    {
        var repo = Repo(("build.ps1", "# build"), ("_common.ps1", "$script:ShortcutName = 'Real.lnk'\n<#\n$script:ShortcutName = 'Old.lnk'\n$script:ReleaseBranch = 'develop'\n#>\n"));

        var found = StableBuildRepo.Find(repo).ShouldNotBeNull();

        found.ShortcutName.ShouldBe("Real.lnk", "the last assignment counts, and the one in the comment is none (#425)");
        found.ReleaseBranch.ShouldBeNull();
    }

    [Fact]
    public void A_common_file_that_cannot_be_read_for_a_moment_keeps_what_was_found_before()
    {
        var repo = Repo(("build.ps1", "# build"), ("_common.ps1", Common));
        var before = StableBuildRepo.Find(repo).ShouldNotBeNull();
        var other = new StableBuildRepo(Path.Combine(_root, "elsewhere"), "Else.lnk");

        using (new FileStream(Path.Combine(repo, "scripts", "_common.ps1"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            StableBuildRepo.Find(repo, [other, before]).ShouldBeSameAs(before, "an editor or a scanner holding the file is no reason to drop the entries (#425)");
            StableBuildRepo.Find(repo).ShouldBeNull("with nothing found before, nothing is made up");
            StableBuildRepo.Find(repo, [other]).ShouldBeNull("what another repository had says nothing about this one");
        }
    }

    [Fact]
    public void A_block_comment_mark_inside_a_line_starts_no_comment()
    {
        // In a string or after a # it is no comment for PowerShell, and the settings below it are real.
        var repo = Repo(("build.ps1", "# build"), ("_common.ps1", "# usage: build.ps1 <#args>\n$script:Marker = '<#'\n$script:Other = \"a`\"<#\"\n$script:ShortcutName = 'App.lnk'\n# done #>\n"));

        StableBuildRepo.Find(repo).ShouldNotBeNull().ShortcutName.ShouldBe("App.lnk");
    }

    [Fact]
    public void A_block_comment_opened_after_code_hides_the_lines_up_to_its_end()
    {
        var repo = Repo(("build.ps1", "# build"), ("_common.ps1", "$script:ShortcutName = 'App.lnk'\n$x = 1 <#\n$script:ReleaseBranch = 'develop'\n#> $script:Late = 'x'\n"));

        StableBuildRepo.Find(repo).ShouldNotBeNull().ReleaseBranch.ShouldBeNull("PowerShell never runs it");
    }

    [Fact]
    public void A_here_string_hides_neither_its_quote_nor_a_comment_after_it()
    {
        // An apostrophe inside a here-string must not leave a quote open, or a commented-out setting below it would count.
        var repo = Repo(("build.ps1", "# build"), ("_common.ps1", "$script:ShortcutName = 'App.lnk'\n$help = @'\nDon't #edit\n'@\n# $script:ShortcutName = 'Old.lnk'\n"));

        StableBuildRepo.Find(repo).ShouldNotBeNull().ShortcutName.ShouldBe("App.lnk");
    }

    [Fact]
    public void A_hash_within_a_word_or_escaped_starts_no_comment()
    {
        StableBuildRepo.WithoutComments("$a = C:\\tools#1 `#tag # gone\n").ShouldBe("$a = C:\\tools#1 `#tag \n");
    }

    [Fact]
    public void Comments_go_and_line_breaks_stay()
    {
        StableBuildRepo.WithoutComments("a # one\nb <# two\nthree #> c\n'#' \"<#\" 'it''s'\n").ShouldBe("a \nb \n c\n'#' \"<#\" 'it''s'\n");
    }

    [Fact]
    public void A_common_file_that_is_gone_keeps_nothing()
    {
        var repo = Repo(("build.ps1", "# build"), ("_common.ps1", Common));
        var before = StableBuildRepo.Find(repo).ShouldNotBeNull();
        File.Delete(Path.Combine(repo, "scripts", "_common.ps1"));

        StableBuildRepo.Find(repo, [before]).ShouldBeNull("only a file held for a moment keeps the last round's scripts");
    }

    [Theory]
    [InlineData(@"C:\Tools\x.lnk")]
    [InlineData(@"..\..\x.lnk")]
    [InlineData("x.exe")]
    public void Find_takes_only_a_bare_shortcut_name(string name)
    {
        var repo = Repo(("build.ps1", "# build"), ("_common.ps1", $"$script:ShortcutName = '{name}'\n"));

        StableBuildRepo.Find(repo).ShouldBeNull();
    }

    [Fact]
    public void Find_returns_null_without_build_ps1()
    {
        StableBuildRepo.Find(Repo(("_common.ps1", Common))).ShouldBeNull();
    }

    [Fact]
    public void Find_returns_null_without_the_shortcut_line()
    {
        StableBuildRepo.Find(Repo(("build.ps1", "# build"), ("_common.ps1", "$script:Other = 'x'\n"))).ShouldBeNull();
    }

    [Fact]
    public void Find_returns_null_for_a_missing_folder()
    {
        StableBuildRepo.Find(Path.Combine(_root, "missing")).ShouldBeNull();
    }

    [Fact]
    public void Find_returns_null_for_a_path_windows_cannot_take()
    {
        StableBuildRepo.Find("c:\\repo\\a\0b").ShouldBeNull();
    }

    [Theory]
    [InlineData("main", "main")]
    [InlineData("develop", "develop")]
    public void A_clean_release_branch_may_build(string branch, string release)
    {
        StableBuildCheck.BuildBlock(Line(branch, 0, release)).ShouldBeNull();
    }

    [Fact]
    public void The_other_of_main_and_develop_is_not_the_release_branch()
    {
        StableBuildCheck.BuildBlock(Line("develop", 0, "main")).ShouldBe("On develop - this repository's stable build runs from main");
    }

    [Theory]
    [InlineData("main")]
    [InlineData("develop")]
    public void Without_a_named_release_branch_main_or_develop_may_build(string branch)
    {
        StableBuildCheck.BuildBlock(Line(branch, 0)).ShouldBeNull();
    }

    [Fact]
    public void Another_branch_is_named_in_the_block()
    {
        StableBuildCheck.BuildBlock(Line("feature/x", 0)).ShouldBe("On feature/x - a stable build runs from main or develop");
    }

    [Theory]
    [InlineData(1, "1 uncommitted change - commit or stash first")]
    [InlineData(3, "3 uncommitted changes - commit or stash first")]
    public void Uncommitted_changes_block_the_build(int dirty, string block)
    {
        StableBuildCheck.BuildBlock(Line("main", dirty, "main")).ShouldBe(block);
    }

    [Theory]
    [InlineData(1, "1 commit behind GitHub - pull first")]
    [InlineData(12, "12 commits behind GitHub - pull first")]
    [InlineData(null, "Behind GitHub - pull first")]
    public void A_release_branch_behind_GitHub_blocks_the_build_as_the_script_would(int? commits, string block)
    {
        StableBuildCheck.BuildBlock(Line("main", 0, "main") with { Behind = new GitBehind(commits) }).ShouldBe(block);
    }

    [Fact]
    public void Uncommitted_changes_are_named_before_being_behind()
    {
        StableBuildCheck.BuildBlock(Line("main", 2, "main") with { Behind = new GitBehind(12) }).ShouldBe("2 uncommitted changes - commit or stash first");
    }

    [Fact]
    public void An_unknown_git_state_blocks_the_build()
    {
        StableBuildCheck.BuildBlock(Line("main", null)).ShouldBe("Git state unknown");
        StableBuildCheck.BuildBlock(Line(null, 0)).ShouldBe("Git state unknown");
    }
}
