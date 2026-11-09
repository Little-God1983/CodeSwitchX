using CodeSwitchX.Core.Persistence;
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
        _pullGit.Ran[0].ShouldStartWith("fetch");
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

        await Eventually(() => panel.ActionMessage is not null && !yard.GitJobs.IsRunning(app.RootPath));
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
        await Eventually(() => !yard.GitJobs.IsRunning(app.RootPath));

        again.PullCommand.Execute(null);

        await Eventually(() => again.ActionMessage == "app is on main, 3 new commits.");
        yard.JobMessage.ShouldBeNull();
    }

    private sealed class FakeSource : IBrowserSource
    {
        public IReadOnlyList<(string Key, string? Executable)> Installed() => [];

        public string? FirefoxProfilesIni() => null;

        public string? ChromiumLocalState(string folder) => null;
    }
}
