using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting.Browsers;
using CodeSwitchX.UI.GitHub;
using CodeSwitchX.UI.Yard;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>The Yard's side of the panel's Pull (#407): which checkout a panel acts on, and where an outcome nobody saw goes.</summary>
public partial class YardGitHubTests
{
    private readonly FakePullGit _pullGit = new();

    private readonly FakeMergeGh _mergeGh = new();

    private static GitHubBrowsers Browsers() =>
        new(new BrowserCatalog(new FakeSource()), Substitute.For<ISettingsStore>(), _ => true, NullLogger<GitHubBrowsers>.Instance);

    [Fact]
    public async Task A_pill_opens_the_panel_on_its_own_lines_checkout()
    {
        var (yard, app) = await SettledYard();
        var line = yard.FindTile(app.Id)!.GitLines[0];

        yard.OpenGitHubListCommand.Execute(line.Issues);

        var panel = yard.GitHubPanel.ShouldNotBeNull();
        panel.PullText.ShouldBe("Pull main", "the line is on main, GitHub's default branch");
        panel.PullCommand.Execute(null);
        await Eventually(() => panel.ActionMessage == "app is on main, 3 new commits.");
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_SSH_COMMAND")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_SSH")))
        {
            return; // an ssh command in the environment always wins: no probe, no batch mode
        }

        // the ssh probe, then the fetch (in batch mode: nothing configures ssh here), before anything else
        _pullGit.Ran[0].ShouldBe("config --get core.sshCommand");
        _pullGit.Ran[1].ShouldStartWith("-c core.sshCommand=ssh -o BatchMode=yes fetch");
    }

    [Fact]
    public async Task A_pull_that_failed_while_its_panel_was_closed_shows_on_the_failure_line()
    {
        var (yard, app) = await SettledYard();
        yard.OpenGitHubLineCommand.Execute(yard.FindTile(app.Id)!.GitLines[0]);
        var panel = yard.GitHubPanel.ShouldNotBeNull();
        _pullGit.Status = " M a.txt\n";
        _pullGit.Hold();
        panel.PullCommand.Execute(null);

        panel.CloseCommand.Execute(null);
        _pullGit.Release();

        await Eventually(() => yard.JobMessage == "app: Pull stopped: 1 file has uncommitted changes.");
        yard.FailureLineText.ShouldBe(yard.JobMessage);
    }

    [Fact]
    public async Task A_timed_round_leaves_a_pulls_failure_on_the_line_and_says_nothing_of_its_own()
    {
        var (yard, app) = await SettledYard();
        yard.OpenGitHubLineCommand.Execute(yard.FindTile(app.Id)!.GitLines[0]);
        var panel = yard.GitHubPanel.ShouldNotBeNull();
        _pullGit.Status = " M a.txt\n";
        _pullGit.Hold();
        panel.PullCommand.Execute(null);
        panel.CloseCommand.Execute(null);
        _pullGit.Release();
        await Eventually(() => yard.JobMessage is not null);
        _gh.Fail = true;
        var before = _gh.Reads;

        _time.Advance(YardViewModel.GitHubRefreshInterval);
        await Eventually(() => _gh.Reads > before);
        await Quiet();

        yard.JobMessage.ShouldBe("app: Pull stopped: 1 file has uncommitted changes.");
        yard.RefreshMessage.ShouldBeNull("a timed round does not start saying what it found");
    }

    [Fact]
    public async Task A_pull_that_failed_in_an_open_panel_stays_off_the_failure_line()
    {
        var (yard, app) = await SettledYard();
        yard.OpenGitHubLineCommand.Execute(yard.FindTile(app.Id)!.GitLines[0]);
        var panel = yard.GitHubPanel.ShouldNotBeNull();
        _pullGit.Status = " M a.txt\n";

        panel.PullCommand.Execute(null);

        await Eventually(() => panel.ActionMessage is not null && !panel.IsBusy);
        panel.IsActionFailed.ShouldBeTrue();
        yard.FailureLineText.ShouldBeNull();
    }

    [Fact]
    public async Task The_open_panel_follows_its_lines_branch_when_git_is_read_again()
    {
        var (yard, app) = await SettledYard();
        yard.OpenGitHubLineCommand.Execute(yard.FindTile(app.Id)!.GitLines[0]);
        var panel = yard.GitHubPanel.ShouldNotBeNull();
        panel.PullText.ShouldBe("Pull main");
        File.WriteAllText(Path.Combine(app.RootPath, ".git", "HEAD"), "ref: refs/heads/feature/y\n");

        await yard.RefreshGitAsync(CancellationToken.None);

        await Eventually(() => panel.PullText == "Switch to main and pull");
    }

    [Fact]
    public async Task A_later_pull_of_the_checkout_takes_its_failure_off_the_line()
    {
        var (yard, app) = await SettledYard();
        yard.OpenGitHubLineCommand.Execute(yard.FindTile(app.Id)!.GitLines[0]);
        var panel = yard.GitHubPanel.ShouldNotBeNull();
        _pullGit.Status = " M a.txt\n";
        _pullGit.Hold();
        panel.PullCommand.Execute(null);
        panel.CloseCommand.Execute(null);
        _pullGit.Release();
        await Eventually(() => yard.JobMessage is not null);
        _pullGit.Status = string.Empty;
        yard.OpenGitHubLineCommand.Execute(yard.FindTile(app.Id)!.GitLines[0]);
        var again = yard.GitHubPanel.ShouldNotBeNull();
        await Eventually(() => !again.IsBusy);

        again.PullCommand.Execute(null);

        await Eventually(() => again.ActionMessage == "app is on main, 3 new commits.");
        // The panel shows the outcome a moment before the Yard hears of it: the job's thread runs both, in that order.
        await Eventually(() => yard.JobMessage is null);
    }

    [Fact]
    public async Task A_merge_reads_the_repository_again_so_the_pills_update_at_once()
    {
        _gh.PullRequests = 1;
        var (yard, app) = await SettledYard();
        yard.FindTile(app.Id)!.GitLines[0].PullRequests.ShouldNotBeNull();
        _gh.PullRequests = 0;
        var line = yard.FindTile(app.Id)!.GitLines[0];

        await yard.GitJobs.MergeAsync(line.Path!, "app", line.GitHub!.Repo, "me", 9, "main", pullBranch: null, () => false);

        _mergeGh.Runs.ShouldHaveSingleItem();
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].PullRequests is null);
    }

    [Fact]
    public async Task A_GitHub_round_does_not_tell_the_panel_a_branch()
    {
        var (yard, app) = await SettledYard();
        yard.OpenGitHubLineCommand.Execute(yard.FindTile(app.Id)!.GitLines[0]);
        var panel = yard.GitHubPanel.ShouldNotBeNull();
        panel.FollowBranch("main-after-a-pull", null);
        _gh.Issues = 7;

        // A GitHub round alone: the timers would run git rounds too, which do know the branch.
        await _activity!.RefreshAsync([app.RootPath], TestContext.Current.CancellationToken);
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Issues?.Count == 7);

        panel.Branch.ShouldBe("main-after-a-pull", "only a git round knows the branch; the lines a GitHub round shows may be older than a pull");
    }

    [Fact]
    public async Task Two_workspaces_on_one_checkout_share_its_pull()
    {
        var root = Repo("app");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        var app = Add("App", root);
        var sub = Add("Sub", Path.Combine(root, "src"));
        var yard = Yard(true, app, sub);
        await yard.InitializeAsync(CancellationToken.None);
        await Eventually(() => yard.FindTile(sub.Id)!.GitLines[0].GitDir is not null && yard.FindTile(app.Id)!.GitLines[0].GitHub is not null
            && yard.FindTile(sub.Id)!.GitLines[0].GitHub is not null);
        var appLine = yard.FindTile(app.Id)!.GitLines[0];
        var subLine = yard.FindTile(sub.Id)!.GitLines[0];
        subLine.GitDir.ShouldBe(appLine.GitDir, "git walks up from the subfolder to the same checkout");
        YardViewModel.SameCheckout(appLine, subLine).ShouldBeTrue();
        _pullGit.Hold();
        yard.OpenGitHubLineCommand.Execute(appLine);
        yard.GitHubPanel.ShouldNotBeNull().PullCommand.Execute(null);

        yard.OpenGitHubLineCommand.Execute(subLine);

        var other = yard.GitHubPanel.ShouldNotBeNull();
        other.IsBusy.ShouldBeTrue("the pull started from the root's line runs on this checkout too");
        other.PullCommand.Execute(null);
        _pullGit.Release();
        await Eventually(() => !other.IsBusy);
        other.ActionMessage.ShouldBe("app is on main, 3 new commits.", "the subfolder's panel shows what the pull of its checkout came to");
        _pullGit.Ran.Count(r => r.Contains("fetch", StringComparison.Ordinal)).ShouldBe(1, "one pull, not two on one .git");
    }

    [Theory]
    [InlineData(@"C:\r\.git", @"C:\r", @"c:\R\.git", @"C:\r\src", true)]
    [InlineData(@"C:\r\.git", @"C:\r", @"C:\s\.git", @"C:\s", false)]
    [InlineData(@"C:\r\.git", @"C:\r", @"C:\x\.git", @"C:\r", true)]
    [InlineData(null, @"C:\r", null, @"c:\R\", true)]
    [InlineData(null, @"C:\r", @"C:\r\.git", @"C:\r\src", false)]
    [InlineData(@"C:\r\.git\worktrees\wt", @"C:\wt", @"C:\r\.git", @"C:\r", true)]
    public void Same_checkout_is_the_same_folder_or_the_same_repository_git_directory(string? gitDirA, string pathA, string? gitDirB, string pathB, bool same) =>
        YardViewModel.SameCheckout(new GitLine(null, "main", 0, pathA, GitDir: gitDirA), new GitLine(null, "main", 0, pathB, GitDir: gitDirB)).ShouldBe(same);

    [Theory]
    [InlineData(null, null)]
    [InlineData(@"C:\r\.git", @"C:\r\.git")]
    [InlineData(@"C:\r\.git\worktrees\wt", @"C:\r\.git")]
    [InlineData(@"C:\r\.git\modules\sub", @"C:\r\.git\modules\sub")]
    [InlineData(@"E:\Repos\worktrees\.git", @"E:\Repos\worktrees\.git")]
    [InlineData(@"E:\bare.git\worktrees\wt", @"E:\bare.git")]
    public void A_linked_worktree_shares_its_repository_s_git_directory(string? gitDir, string? common) =>
        YardGitJobs.CommonGitDir(gitDir).ShouldBe(common);

    [Fact]
    public async Task An_outcome_is_only_for_a_panel_of_the_working_tree_the_job_ran_in()
    {
        var jobs = yardJobs();
        await jobs.PullAsync(@"C:\wt", "main", "app", () => false, key: @"C:\r\.git", tree: @"C:\r\.git\worktrees\wt");

        jobs.TakeOutcome(@"C:\r\.git", @"C:\r\.git").ShouldBeNull("the main checkout did not pull, its linked worktree did");
        jobs.TakeOutcome(@"C:\r\.git", @"c:\R\.git\worktrees\WT").ShouldNotBeNull().Message.ShouldBe("app is on main, 3 new commits.");

        YardGitJobs yardJobs() => new(new GitPuller(_pullGit.RunAsync), new ImmediateDispatcher(), _time, NullLogger.Instance);
    }

    private sealed class FakeSource : IBrowserSource
    {
        public IReadOnlyList<(string Key, string? Executable)> Installed() => [];

        public string? FirefoxProfilesIni() => null;

        public string? ChromiumLocalState(string folder) => null;
    }
}
