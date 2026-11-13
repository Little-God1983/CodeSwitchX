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
    public async Task A_workspace_on_a_subfolder_gets_the_scripts_at_the_top_git_found()
    {
        // One walk up finds the repository (#425): git's, at whose end the scripts are looked for.
        var root = Repo("app");
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        File.WriteAllText(Path.Combine(root, "scripts", "build.ps1"), "# build");
        File.WriteAllText(Path.Combine(root, "scripts", "_common.ps1"), "$script:ShortcutName = 'App.lnk'\n");
        var src = Path.Combine(root, "src");
        Directory.CreateDirectory(Path.Combine(src, "scripts"));
        File.WriteAllText(Path.Combine(src, "scripts", "build.ps1"), "# not the stable build");
        File.WriteAllText(Path.Combine(src, "scripts", "_common.ps1"), "$script:ShortcutName = 'Other.lnk'\n");
        var app = new Workspace { Name = "App", RootPath = src, TrackId = _general.Id };
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([app]));
        var yard = Yard((_, _, _) => Task.FromResult<string?>(string.Empty));
        await yard.InitializeAsync(CancellationToken.None);

        await yard.RefreshGitAsync(CancellationToken.None);

        var found = yard.FindTile(app.Id)!.GitLines.ShouldHaveSingleItem().StableBuild.ShouldNotBeNull();
        found.RepoPath.ShouldBe(root);
        found.ShortcutName.ShouldBe("App.lnk");
    }

    [Fact]
    public async Task A_tile_whose_menu_opens_is_read_at_once_and_alone()
    {
        // The precheck reads the tile's lines, which can be up to a round old: its git is read again at once, and only its (#425).
        var first = new Workspace { Name = "First", RootPath = Repo("first"), TrackId = _general.Id };
        var opened = new Workspace { Name = "Opened", RootPath = Repo("opened"), TrackId = _general.Id };
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([first, opened]));
        var read = new List<string>();
        var yard = Yard((dir, args, _) =>
        {
            if (args.StartsWith("status", StringComparison.Ordinal))
            {
                lock (read)
                {
                    read.Add(Path.GetFileName(dir));
                }
            }

            return Task.FromResult<string?>(string.Empty);
        });
        await yard.InitializeAsync(CancellationToken.None);
        await yard.RefreshGitAsync(CancellationToken.None);
        read.Clear();

        yard.RefreshGitOf(yard.FindTile(opened.Id)!);
        await yard.CurrentTileGitRefresh;

        read.ShouldBe(["opened"]);
    }

    [Fact]
    public async Task A_round_that_started_before_the_menus_read_does_not_put_older_lines_back()
    {
        var app = new Workspace { Name = "App", RootPath = Repo("app"), TrackId = _general.Id };
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([app]));
        var status = "";
        TaskCompletionSource? gate = null;
        var entered = new TaskCompletionSource();
        var yard = Yard(async (_, args, _) =>
        {
            if (!args.StartsWith("status", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            var answer = status; // what git sees as the call starts
            if (Interlocked.Exchange(ref gate, null) is { } held)
            {
                entered.SetResult();
                await held.Task;
            }

            return answer;
        });
        await yard.InitializeAsync(CancellationToken.None);
        await yard.RefreshGitAsync(CancellationToken.None);
        var held = new TaskCompletionSource();
        gate = held;
        var round = yard.RefreshGitAsync(CancellationToken.None); // reads "clean", and is held there
        await entered.Task;
        status = "? a.cs\n? b.cs\n? c.cs\n";                      // the user's edits land meanwhile

        yard.RefreshGitOf(yard.FindTile(app.Id)!);
        await yard.CurrentTileGitRefresh;
        yard.FindTile(app.Id)!.GitLines[0].DirtyCount.ShouldBe(3);
        held.SetResult();
        await round;

        yard.FindTile(app.Id)!.GitLines[0].DirtyCount.ShouldBe(3, "the round's read began before the menu's, so it knows less");
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
