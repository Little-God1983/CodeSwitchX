using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Yard;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>#452: the git round works out whether the installed stable build is behind GitHub's default branch.</summary>
public partial class YardGitHubTests
{
    private const string BuiltFrom = "715bed9";

    private const string BuiltFromFull = "715bed90aa11defa35d0aa11defa35d0aa11defa";

    /// <summary>A record of <c>git log --format=%x1e%H%x1f%s --name-only</c>.</summary>
    private static string LogRecord(string subject, params string[] files) =>
        "\x1e" + new string('c', 40) + "\x1f" + subject + "\n\n" + string.Join("\n", files) + "\n";

    private static readonly string BumpOnly = LogRecord("chore: bump version to 1.0.0.6", "Directory.Build.props");

    /// <summary>
    /// Git with a clean tree whose commit it does not say (so the behind pill asks git nothing), the build's commit and GitHub's here, and
    /// <paramref name="log"/> as the commits between them.
    /// </summary>
    /// <param name="hasBuildCommit">False: the build was made from a commit this clone has not got.</param>
    private void GitHasTheBuild(string log, bool hasBuildCommit = true) => _gitRunner = args =>
    {
        lock (_gitRan)
        {
            _gitRan.Add(args);
        }

        return args.StartsWith("rev-list --no-walk --ignore-missing " + BuiltFrom, StringComparison.Ordinal) ? (hasBuildCommit ? BuiltFromFull : string.Empty) + "\n"
            : args.StartsWith("rev-list --no-walk", StringComparison.Ordinal) ? args[(args.LastIndexOf(' ') + 1)..] + "\n"
            : args.StartsWith("log --format=", StringComparison.Ordinal) ? log
            : string.Empty;
    };

    private int GitAsked(string start)
    {
        lock (_gitRan)
        {
            return _gitRan.Count(r => r.StartsWith(start, StringComparison.Ordinal));
        }
    }

    /// <summary>The app's repository with the stable-build scripts and an installed build 1.0.0.5 made from <see cref="BuiltFrom"/>; the marker's path.</summary>
    private string StableApp(out Workspace app)
    {
        var root = Repo("app");
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        File.WriteAllText(Path.Combine(root, "scripts", "build.ps1"), "# build");
        var installRoot = Path.Combine(_tempRoot, "stable");
        File.WriteAllText(Path.Combine(root, "scripts", "_common.ps1"),
            $"$script:ShortcutName = 'App.lnk'\n$script:DefaultInstallRoot = '{installRoot}'\n$script:AppName = 'App'\n$script:InstallMarkerName = 'app-install.json'\n");
        Directory.CreateDirectory(Path.Combine(installRoot, "App"));
        var marker = Path.Combine(installRoot, "App", "app-install.json");
        File.WriteAllText(marker, $$"""{"version":"1.0.0.5","complete":true,"commit":"{{BuiltFrom}}"}""");
        app = Add("App", root);
        return marker;
    }

    /// <summary>A Yard whose app line has been compared with GitHub's head once.</summary>
    private async Task<(YardViewModel Yard, Workspace App, string Marker)> ComparedStableYard()
    {
        _gh.DefaultHead = OnGitHub;
        var marker = StableApp(out var app);
        var yard = Yard(true, app);
        await yard.InitializeAsync(CancellationToken.None);
        // No timer is advanced: the read of GitHub asks git again by itself.
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Stable is { Freshness: not StableFreshness.Unknown });
        await Quiet();
        await yard.CurrentGitRefresh;
        return (yard, app, marker);
    }

    [Fact]
    public async Task The_round_shows_an_up_to_date_build()
    {
        GitHasTheBuild(BumpOnly);

        var (yard, app, _) = await ComparedStableYard();

        yard.FindTile(app.Id)!.GitLines[0].Stable.ShouldBe(new StableBuildStatus(StableFreshness.UpToDate, "1.0.0.5", 0, "main", BuiltFrom, OnGitHub));
        GitAsked($"log --format=%x1e%H%x1f%s --name-only {BuiltFromFull}..{OnGitHub}").ShouldBeGreaterThan(0, "GitHub's default branch, not the checkout's");
    }

    [Fact]
    public async Task The_round_shows_an_outdated_build_with_its_count()
    {
        GitHasTheBuild(BumpOnly + LogRecord("feat: one", "a.cs") + LogRecord("fix: two", "b.cs"));

        var (yard, app, _) = await ComparedStableYard();

        var stable = yard.FindTile(app.Id)!.GitLines[0].Stable.ShouldNotBeNull();
        stable.Freshness.ShouldBe(StableFreshness.Outdated);
        stable.NewerCommits.ShouldBe(2);
        stable.Label.ShouldBe("outdated");
    }

    [Fact]
    public async Task The_same_two_commits_are_not_compared_again_next_round()
    {
        GitHasTheBuild(BumpOnly);
        var (yard, app, _) = await ComparedStableYard();
        var logs = GitAsked("log --format=");

        await yard.RefreshGitAsync(CancellationToken.None);

        GitAsked("log --format=").ShouldBe(logs, "neither the build's commit nor GitHub's head changed");
        yard.FindTile(app.Id)!.GitLines[0].Stable!.Freshness.ShouldBe(StableFreshness.UpToDate);
    }

    [Fact]
    public async Task A_locked_marker_keeps_what_the_line_showed()
    {
        GitHasTheBuild(BumpOnly);
        var (yard, app, marker) = await ComparedStableYard();
        var shown = yard.FindTile(app.Id)!.GitLines[0].Stable;

        using (new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await yard.RefreshGitAsync(CancellationToken.None);
        }

        yard.FindTile(app.Id)!.GitLines[0].Stable.ShouldBe(shown, "the build writing it, or a scanner, is no reason to say \"no stable build\"");
    }

    [Fact]
    public async Task A_build_git_cannot_compare_shows_its_version_and_is_not_read_again_and_again()
    {
        // Made from a commit this clone has not got: git never answers, and the tile must not be read again after every read.
        _gh.DefaultHead = OnGitHub;
        GitHasTheBuild(BumpOnly, hasBuildCommit: false);
        StableApp(out var app);
        var yard = Yard(true, app);
        await yard.InitializeAsync(CancellationToken.None);
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].GitHub?.DefaultHead is not null);
        await Quiet();
        await yard.CurrentGitRefresh;
        var reads = GitAsked("status");

        await Task.Delay(500, TestContext.Current.CancellationToken);

        GitAsked("status").ShouldBe(reads);
        yard.FindTile(app.Id)!.GitLines[0].Stable.ShouldBe(new StableBuildStatus(StableFreshness.Unknown, "1.0.0.5", BuiltFrom: BuiltFrom));
    }

    [Fact]
    public async Task A_new_head_on_GitHub_reads_git_again()
    {
        GitHasTheBuild(BumpOnly);
        var (yard, app, _) = await ComparedStableYard();
        var moved = new string('b', 40);

        // A merge on GitHub: only the stable build's row is out of date, as the line knows no commit of its own to be behind with.
        _gh.DefaultHead = moved;
        await _activity!.RefreshAsync([app.RootPath], TestContext.Current.CancellationToken);

        // No timer is advanced and no round asked for: the changed read of GitHub asks git again by itself.
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Stable?.ComparedWith == moved);
        GitAsked($"log --format=%x1e%H%x1f%s --name-only {BuiltFromFull}..{moved}").ShouldBe(1);
    }
}
