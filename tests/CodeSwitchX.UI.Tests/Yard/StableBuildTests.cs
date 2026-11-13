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

    /// <summary>A repository folder whose scripts folder holds the given files, each with the given text.</summary>
    private string Repo(params (string Name, string Text)[] scripts)
    {
        var dir = Path.Combine(_root, "scripts");
        Directory.CreateDirectory(dir);
        foreach (var (name, text) in scripts)
        {
            File.WriteAllText(Path.Combine(dir, name), text);
        }

        return _root;
    }

    private const string Common = "$script:DefaultInstallRoot = 'E:\\StableVersion'\r\n$script:ShortcutName       = 'Some App.lnk'\r\n";

    [Fact]
    public void Find_reads_the_shortcut_name_from_common()
    {
        var repo = Repo(("build.ps1", "# build"), ("_common.ps1", Common));

        var found = StableBuildRepo.Find(repo).ShouldNotBeNull();

        found.ShortcutName.ShouldBe("Some App.lnk");
        found.RepoPath.ShouldBe(repo);
        found.BuildScript.ShouldBe(Path.Combine(repo, "scripts", "build.ps1"));
    }

    [Fact]
    public void Find_accepts_the_name_in_double_quotes()
    {
        var repo = Repo(("build.ps1", "# build"), ("_common.ps1", "  $script:ShortcutName = \"X.lnk\"\n"));

        StableBuildRepo.Find(repo).ShouldNotBeNull().ShortcutName.ShouldBe("X.lnk");
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

    [Theory]
    [InlineData("main")]
    [InlineData("develop")]
    public void A_clean_main_or_develop_may_build(string branch)
    {
        StableBuildCheck.BuildBlock(new GitLine(null, branch, 0)).ShouldBeNull();
    }

    [Fact]
    public void Another_branch_is_named_in_the_block()
    {
        StableBuildCheck.BuildBlock(new GitLine(null, "feature/x", 0)).ShouldBe("On feature/x - a stable build runs from main or develop");
    }

    [Theory]
    [InlineData(1, "1 uncommitted change - commit or stash first")]
    [InlineData(3, "3 uncommitted changes - commit or stash first")]
    public void Uncommitted_changes_block_the_build(int dirty, string block)
    {
        StableBuildCheck.BuildBlock(new GitLine(null, "main", dirty)).ShouldBe(block);
    }

    [Fact]
    public void A_line_carried_over_from_the_last_round_keeps_its_scripts_without_reading_the_folder()
    {
        // A line the round carried over is one whose folder could not be checked, an offline share say: each look there
        // blocks for about 20 s, one after another.
        var scripts = new StableBuildRepo(@"\\nas\share\app", "App.lnk");
        var carried = new GitLine("app", "main", 0, @"\\nas\share\app", IsRepository: true, StableBuild: scripts);
        var fresh = new GitLine("tool", "main", 0, @"c:\repo\tool", IsRepository: true);
        var looked = new List<string>();

        var lines = StableBuildRepo.Mark([carried, fresh], previous: [carried], path =>
        {
            looked.Add(path);
            return new StableBuildRepo(path, "Tool.lnk");
        });

        looked.ShouldBe([@"c:\repo\tool"]);
        lines[0].StableBuild.ShouldBe(scripts);
        lines[1].StableBuild.ShouldNotBeNull().ShortcutName.ShouldBe("Tool.lnk");
    }

    [Fact]
    public void A_line_without_a_repository_has_no_scripts()
    {
        var lines = StableBuildRepo.Mark([new GitLine(null, null, null, @"c:\plain")], previous: [], _ => throw new InvalidOperationException("not looked at"));

        lines.ShouldHaveSingleItem().StableBuild.ShouldBeNull();
    }

    [Fact]
    public void An_unknown_git_state_blocks_the_build()
    {
        StableBuildCheck.BuildBlock(new GitLine(null, "main", null)).ShouldBe("Git state unknown");
        StableBuildCheck.BuildBlock(new GitLine(null, null, 0)).ShouldBe("Git state unknown");
    }
}
