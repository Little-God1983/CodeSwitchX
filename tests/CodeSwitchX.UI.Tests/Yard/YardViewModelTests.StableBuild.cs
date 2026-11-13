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
}
