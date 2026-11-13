using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Yard;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>#423: the git round finds the stable-build scripts of each repository line.</summary>
public partial class YardViewModelTests
{
    [Fact]
    public async Task The_git_round_marks_a_repository_with_the_stable_build_scripts()
    {
        var root = Repo("app");
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        File.WriteAllText(Path.Combine(root, "scripts", "build.ps1"), "# build");
        File.WriteAllText(Path.Combine(root, "scripts", "_common.ps1"), "$script:ShortcutName = 'App.lnk'\n");
        var app = new Workspace { Name = "App", RootPath = root, TrackId = _general.Id };
        var plain = new Workspace { Name = "Shop", RootPath = Repo("shop"), TrackId = _general.Id };
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([app, plain]));
        var yard = Yard((_, _, _) => Task.FromResult<string?>(string.Empty));
        await yard.InitializeAsync(CancellationToken.None);

        await yard.RefreshGitAsync(CancellationToken.None);

        yard.FindTile(app.Id)!.GitLines.ShouldHaveSingleItem().StableBuild.ShouldNotBeNull().ShortcutName.ShouldBe("App.lnk");
        yard.FindTile(plain.Id)!.GitLines.ShouldHaveSingleItem().StableBuild.ShouldBeNull();
    }

    [Fact]
    public async Task A_line_the_round_carries_over_keeps_its_scripts_without_looking_again()
    {
        // A carried line is one whose folder could not be checked, an offline share say, where every look blocks for about 20 s.
        var nuget = Path.Combine(_tempRoot, "nuget");
        var llm = Repo(Path.Combine("nuget", "llm"));
        Directory.CreateDirectory(Path.Combine(llm, "scripts"));
        File.WriteAllText(Path.Combine(llm, "scripts", "build.ps1"), "# build");
        File.WriteAllText(Path.Combine(llm, "scripts", "_common.ps1"), "$script:ShortcutName = 'Llm.lnk'\n");
        Repo(Path.Combine("nuget", "speech"));
        var file = Path.Combine(_tempRoot, "Nuget.code-workspace");
        File.WriteAllText(file, """{ "folders": [ { "path": "nuget" } ] }""");
        var listed = new Workspace { Name = "Nuget", RootPath = nuget, WorkspaceFile = file, TrackId = _general.Id };
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([listed]));
        var yard = Yard((_, _, _) => Task.FromResult<string?>(string.Empty));
        await yard.InitializeAsync(CancellationToken.None);
        await yard.RefreshGitAsync(CancellationToken.None);
        Directory.Delete(Path.Combine(llm, "scripts"), recursive: true);

        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await yard.RefreshGitAsync(CancellationToken.None);
        }

        yard.FindTile(listed.Id)!.GitLines[0].StableBuild.ShouldNotBeNull("the line was carried over, so its folder was not looked at again")
            .ShortcutName.ShouldBe("Llm.lnk");
    }
}
