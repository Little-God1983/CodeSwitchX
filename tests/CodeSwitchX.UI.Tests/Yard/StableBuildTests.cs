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
    public void Find_looks_at_the_top_of_the_repository_for_a_folder_inside_it()
    {
        var repo = Repo(("build.ps1", "# build"), ("_common.ps1", Common));
        var src = Path.Combine(repo, "src");
        Directory.CreateDirectory(Path.Combine(src, "scripts"));
        File.WriteAllText(Path.Combine(src, "scripts", "build.ps1"), "# not the stable build");
        File.WriteAllText(Path.Combine(src, "scripts", "_common.ps1"), "$script:ShortcutName = 'Other.lnk'\n");

        var found = StableBuildRepo.Find(src).ShouldNotBeNull();

        found.RepoPath.ShouldBe(repo);
        found.ShortcutName.ShouldBe("Some App.lnk");
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

    [Fact]
    public void An_unknown_git_state_blocks_the_build()
    {
        StableBuildCheck.BuildBlock(Line("main", null)).ShouldBe("Git state unknown");
        StableBuildCheck.BuildBlock(Line(null, 0)).ShouldBe("Git state unknown");
    }
}
