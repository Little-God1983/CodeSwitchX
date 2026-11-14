using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Yard;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>A line on the default branch says when it is behind GitHub (#426).</summary>
public partial class YardGitHubTests
{
    private const string Here = "defa35d0aa11defa35d0aa11defa35d0aa11defa";

    private const string OnGitHub = "7d6783b6e4ca65062d1ab6cdfe82e5b5f29e0728";

    private readonly List<string> _gitRan = [];

    /// <summary>Git with the checkout at <paramref name="head"/>, a clean tree, GitHub's commit fetched and <paramref name="behind"/> commits short of it.</summary>
    private void GitIsAt(string head, int behind = 0) => _gitRunner = args =>
    {
        lock (_gitRan)
        {
            _gitRan.Add(args);
        }

        return args.StartsWith("status", StringComparison.Ordinal) ? $"# branch.oid {head}\n# branch.head main\n"
            : args.StartsWith("rev-list --no-walk", StringComparison.Ordinal) ? args[(args.LastIndexOf(' ') + 1)..] + "\n"
            : args.StartsWith("rev-list --count HEAD..", StringComparison.Ordinal) ? $"{behind}\n"
            : args.StartsWith("rev-parse --is-shallow-repository", StringComparison.Ordinal) ? "false\n"
            : string.Empty;
    };

    private bool GitWasAsked(string start)
    {
        lock (_gitRan)
        {
            return _gitRan.Any(r => r.StartsWith(start, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_line_on_the_default_branch_that_lacks_GitHubs_commit_is_behind_not_clean()
    {
        _gh.DefaultHead = OnGitHub;
        GitIsAt(Here, behind: 12);
        var app = Add("App", Repo("app"));
        var yard = Yard(true, app);

        await yard.InitializeAsync(CancellationToken.None);

        // No timer is advanced: the read of GitHub asks git again by itself.
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Behind is not null);
        var line = yard.FindTile(app.Id)!.GitLines[0];
        line.Behind.ShouldBe(new GitBehind(12));
        line.DirtyCount.ShouldBe(0);
        line.GitStateLabel.ShouldBeNull("a branch that is behind is not clean");
        line.BehindLabel.ShouldBe("12 behind");
    }

    [Fact]
    public async Task A_line_at_GitHubs_commit_is_clean()
    {
        _gh.DefaultHead = OnGitHub;
        GitIsAt(OnGitHub);
        var (yard, app) = await SettledYard();

        await yard.RefreshGitAsync(CancellationToken.None);

        var line = yard.FindTile(app.Id)!.GitLines[0];
        line.Head.ShouldBe(OnGitHub);
        line.Behind.ShouldBeNull();
        line.GitStateLabel.ShouldBe("clean");
        GitWasAsked("rev-list").ShouldBeFalse("the same commit needs no question");
    }

    [Fact]
    public async Task The_answer_for_the_same_two_commits_is_not_asked_again_every_round()
    {
        _gh.DefaultHead = OnGitHub;
        GitIsAt(Here, behind: 12);
        var (yard, app) = await SettledYard();
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Behind is not null);
        await yard.CurrentGitRefresh;
        lock (_gitRan)
        {
            _gitRan.Clear();
        }

        await yard.RefreshGitAsync(CancellationToken.None);

        yard.FindTile(app.Id)!.GitLines[0].BehindLabel.ShouldBe("12 behind");
        GitWasAsked("status").ShouldBeTrue();
        GitWasAsked("rev-list").ShouldBeFalse("neither commit changed");
    }

    [Fact]
    public async Task A_pull_makes_a_line_that_was_behind_clean_again()
    {
        _gh.DefaultHead = OnGitHub;
        GitIsAt(Here, behind: 12);
        var (yard, app) = await SettledYard();
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Behind is not null);

        GitIsAt(OnGitHub);
        await yard.RefreshGitAsync(CancellationToken.None);

        var line = yard.FindTile(app.Id)!.GitLines[0];
        line.Behind.ShouldBeNull();
        line.GitStateLabel.ShouldBe("clean");
    }

    [Fact]
    public async Task A_line_on_another_branch_is_not_compared_with_the_default_branch()
    {
        _gh.DefaultHead = OnGitHub;
        GitIsAt(Here, behind: 12);
        var root = Repo("app");
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/feature/x\n");
        var app = Add("App", root);
        var yard = Yard(true, app);
        await yard.InitializeAsync(CancellationToken.None);
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].GitHub?.DefaultHead is not null);
        await Quiet();

        await yard.RefreshGitAsync(CancellationToken.None);

        var line = yard.FindTile(app.Id)!.GitLines[0];
        line.Behind.ShouldBeNull();
        line.GitStateLabel.ShouldBe("clean");
        GitWasAsked("rev-list").ShouldBeFalse();
    }

    [Fact]
    public async Task A_line_on_another_branch_that_lacks_commits_its_branch_has_on_GitHub_is_behind()
    {
        // A repository whose work merges into develop (#428): the line on develop is compared with GitHub's develop.
        _gh.DefaultHead = new string('a', 40);
        _gh.Branches["develop"] = OnGitHub;
        GitIsAt(Here, behind: 4);
        var app = Add("App", Repo("app"));
        File.WriteAllText(Path.Combine(app.RootPath, ".git", "HEAD"), "ref: refs/heads/develop\n");
        var yard = Yard(true, app);

        await yard.InitializeAsync(CancellationToken.None);

        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Behind is not null);
        var line = yard.FindTile(app.Id)!.GitLines[0];
        line.BehindLabel.ShouldBe("4 behind");
        line.GitStateLabel.ShouldBeNull("a branch that is behind is not clean");
        line.BehindAgainst.ShouldBe(OnGitHub);
    }

    [Fact]
    public async Task A_switch_to_a_branch_the_last_read_did_not_ask_about_reads_GitHub_for_it_at_once()
    {
        // The read of GitHub asks about the branches the folders are on; after a switch, the next one is up to ten minutes away (#428).
        _gh.DefaultHead = OnGitHub;
        _gh.Branches["develop"] = OnGitHub;
        GitIsAt(Here, behind: 4);
        var (yard, app) = await SettledYard();
        await yard.CurrentGitRefresh;
        _gh.BranchesAsked.ShouldNotContain("develop");

        File.WriteAllText(Path.Combine(yard.FindTile(app.Id)!.RootPath, ".git", "HEAD"), "ref: refs/heads/develop\n");
        await yard.RefreshGitAsync(CancellationToken.None);

        // No timer is advanced and Refresh is not pressed.
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Behind is not null);
        yard.FindTile(app.Id)!.GitLines[0].BehindLabel.ShouldBe("4 behind");
        lock (_gh.Names)
        {
            _gh.BranchesAsked.Count(b => b == "develop").ShouldBe(1);
        }
    }

    [Fact]
    public async Task A_branch_GitHub_has_not_got_is_asked_about_once_and_the_line_stays_clean()
    {
        GitIsAt(Here);
        _gh.DefaultHead = OnGitHub;
        var (yard, app) = await SettledYard();
        File.WriteAllText(Path.Combine(yard.FindTile(app.Id)!.RootPath, ".git", "HEAD"), "ref: refs/heads/local-only\n");

        await yard.RefreshGitAsync(CancellationToken.None);
        await Eventually(() => _gh.BranchesAsked.Contains("local-only"));
        await Quiet();
        await yard.RefreshGitAsync(CancellationToken.None);
        await yard.RefreshGitAsync(CancellationToken.None);
        await Quiet();

        lock (_gh.Names)
        {
            _gh.BranchesAsked.Count(b => b == "local-only").ShouldBe(1, "not every 30 s");
        }

        yard.FindTile(app.Id)!.GitLines[0].GitStateLabel.ShouldBe("clean", "nothing on GitHub to be behind");
    }

    [Fact]
    public async Task A_detached_HEAD_is_not_taken_for_a_branch_on_GitHub()
    {
        // Git names a detached HEAD by the start of its commit, which moves at every step of a rebase (review of #428).
        _gh.DefaultHead = OnGitHub;
        GitIsAt(Here);
        var (yard, app) = await SettledYard();
        File.WriteAllText(Path.Combine(yard.FindTile(app.Id)!.RootPath, ".git", "HEAD"), Here + "\n");

        await yard.RefreshGitAsync(CancellationToken.None);
        await Quiet();
        await yard.RefreshCommand.ExecuteAsync(null);
        await Quiet();

        lock (_gh.Names)
        {
            _gh.BranchesAsked.ShouldNotContain(Here[..7]);
        }
    }

    [Fact]
    public async Task Without_GitHubs_commit_a_line_says_nothing_about_being_behind()
    {
        GitIsAt(Here, behind: 12);
        var (yard, app) = await SettledYard();

        await yard.RefreshGitAsync(CancellationToken.None);

        yard.FindTile(app.Id)!.GitLines[0].Behind.ShouldBeNull();
        yard.FindTile(app.Id)!.GitLines[0].GitStateLabel.ShouldBe("clean");
        GitWasAsked("rev-list").ShouldBeFalse();
    }

    [Fact]
    public async Task A_line_git_could_not_compare_yet_is_not_called_clean()
    {
        // "clean" stands only where nothing is left to do; with no answer from git, nobody knows (#429).
        _gh.DefaultHead = OnGitHub;
        _gitRunner = args => args.StartsWith("status", StringComparison.Ordinal) ? $"# branch.oid {Here}\n# branch.head main\n"
            : args.StartsWith("rev-list", StringComparison.Ordinal) ? null
            : string.Empty;
        var (yard, app) = await SettledYard();

        await yard.RefreshGitAsync(CancellationToken.None);

        var line = yard.FindTile(app.Id)!.GitLines[0];
        line.Behind.ShouldBeNull();
        line.GitStateLabel.ShouldBeNull();
        line.BehindLabel.ShouldBeNull();
    }

    [Fact]
    public async Task A_build_pressed_reads_GitHub_before_it_judges_the_line()
    {
        // A merge made on github.com: the last read of GitHub is up to ten minutes old, and the press must not go by it (#429).
        _gh.DefaultHead = OnGitHub;
        GitIsAt(OnGitHub);
        var (yard, app) = await SettledYard();
        var root = yard.FindTile(app.Id)!.RootPath;
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        File.WriteAllText(Path.Combine(root, "scripts", "build.ps1"), "# build");
        File.WriteAllText(Path.Combine(root, "scripts", "_common.ps1"), "$script:ShortcutName = 'App.lnk'\n$script:ReleaseBranch = 'main'\n");
        await yard.RefreshGitAsync(CancellationToken.None);
        var line = yard.FindTile(app.Id)!.GitLines[0];
        StableBuildCheck.BuildBlock(line).ShouldBeNull("as last read, the line is at GitHub's commit");
        _gh.DefaultHead = new string('a', 40);
        GitIsAt(OnGitHub, behind: 3);

        var fresh = await yard.FreshLineAsync(line);

        StableBuildCheck.BuildBlock(fresh.ShouldNotBeNull()).ShouldBe("3 commits behind GitHub - pull first");
        fresh.GitHub.ShouldNotBeNull().DefaultHead.ShouldNotBeNull().Sha.ShouldBe(new string('a', 40), "the line goes with the read it waited for");
    }

    [Fact]
    public async Task A_round_that_compared_with_a_commit_GitHub_moved_past_asks_git_again_at_once()
    {
        // The checkout switches to the default branch while a round runs, and GitHub's commit moves in the same moment (#429): the read of
        // GitHub sees the line still on its other branch, and only the round's own lines can tell.
        _gh.DefaultHead = OnGitHub;
        GitIsAt(Here, behind: 1);
        var app = Add("App", Repo("app"));
        File.WriteAllText(Path.Combine(app.RootPath, ".git", "HEAD"), "ref: refs/heads/feature\n");
        var yard = Yard(true, app);
        await yard.InitializeAsync(CancellationToken.None);
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Issues is not null);
        await Quiet();
        await yard.CurrentGitRefresh;

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var held = 0;
        var git = _gitRunner!;
        _gitRunner = args =>
        {
            if (args.StartsWith("rev-list --no-walk", StringComparison.Ordinal) && Interlocked.Exchange(ref held, 1) == 0)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }

            return git(args);
        };
        File.WriteAllText(Path.Combine(app.RootPath, ".git", "HEAD"), "ref: refs/heads/main\n");
        var round = yard.RefreshGitAsync(CancellationToken.None);
        entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ShouldBeTrue("the round compares the line with GitHub's commit as it was");
        var moved = new string('b', 40);
        _gh.DefaultHead = moved;
        await _activity!.RefreshAsync([app.RootPath], TestContext.Current.CancellationToken);
        release.Set();
        await round;

        // No timer is advanced: the round's own lines ask again.
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].BehindAgainst == moved);
    }

    [Fact]
    public async Task A_default_branch_that_moved_on_GitHub_shows_on_the_line_without_waiting_for_the_git_timer()
    {
        _gh.DefaultHead = OnGitHub;
        GitIsAt(OnGitHub);
        var (yard, app) = await SettledYard();
        await yard.RefreshGitAsync(CancellationToken.None);
        yard.FindTile(app.Id)!.GitLines[0].Behind.ShouldBeNull();

        // A pull request was merged on GitHub: Refresh reads git first, then GitHub, which is where the news comes from.
        _gh.DefaultHead = new string('a', 40);
        GitIsAt(OnGitHub, behind: 3);
        await yard.RefreshCommand.ExecuteAsync(null);

        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Behind is not null);
        yard.FindTile(app.Id)!.GitLines[0].BehindLabel.ShouldBe("3 behind");
    }
}
