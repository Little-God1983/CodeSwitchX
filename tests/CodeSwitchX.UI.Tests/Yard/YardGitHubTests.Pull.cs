using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Hosting.Browsers;
using CodeSwitchX.UI.GitHub;
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
        _pullGit.Ran[0].ShouldStartWith("status");
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

        await Eventually(() => yard.RefreshMessage == "Pull stopped: 1 file has uncommitted changes.");
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
        yard.RefreshMessage.ShouldBeNull();
    }

    private sealed class FakeSource : IBrowserSource
    {
        public IReadOnlyList<(string Key, string? Executable)> Installed() => [];

        public string? FirefoxProfilesIni() => null;

        public string? ChromiumLocalState(string folder) => null;
    }
}
