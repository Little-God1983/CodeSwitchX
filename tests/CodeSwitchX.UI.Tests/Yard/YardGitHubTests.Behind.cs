using CodeSwitchX.Core.Workspaces;

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
