using System.Diagnostics;
using CodeSwitchX.Core.GitHub;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Hosting.Browsers;
using CodeSwitchX.UI.GitHub;
using CodeSwitchX.UI.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Settings;

public class GitHubPageViewModelTests
{
    private readonly IGhCli _gh = Substitute.For<IGhCli>();
    private readonly ISettingsStore _store = Substitute.For<ISettingsStore>();
    private readonly List<ProcessStartInfo> _started = [];
    private Func<ProcessStartInfo, bool> _start;
    private readonly GitHubBrowsers _browsers;
    private readonly GitHubPageViewModel _page;

    public GitHubPageViewModelTests()
    {
        _start = info => { _started.Add(info); return true; };
        _gh.AccountsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>(["someone", "other"]));
        var catalog = new BrowserCatalog(new FakeSource());
        _browsers = new GitHubBrowsers(catalog, _store, info => _start(info), NullLogger<GitHubBrowsers>.Instance);
        _page = new GitHubPageViewModel(_gh, _browsers, catalog);
    }

    [Fact]
    public async Task Each_gh_account_gets_a_row()
    {
        _browsers.SetChoice("other", "firefox|alpha");

        await _page.RefreshAsync(CancellationToken.None);

        _page.Accounts.Select(a => (a.Login, a.SelectedId)).ShouldBe([("someone", BrowserEntry.DefaultId), ("other", "firefox|alpha")]);
        _page.Accounts[0].Choices.Select(c => c.Id).ShouldBe([BrowserEntry.DefaultId, "firefox|alpha", "firefox|beta"]);
        _page.HasNoAccounts.ShouldBeFalse();
    }

    [Fact]
    public async Task Picking_a_browser_saves_it()
    {
        await _page.RefreshAsync(CancellationToken.None);

        _page.Accounts[0].SelectedId = "firefox|beta";

        _browsers.ChoiceFor("someone").ShouldBe("firefox|beta");
        _browsers.ChoiceFor("other").ShouldBe(BrowserEntry.DefaultId);
    }

    [Fact]
    public async Task Test_opens_the_profile_page_in_the_chosen_browser()
    {
        await _page.RefreshAsync(CancellationToken.None);
        _page.Accounts[1].SelectedId = "firefox|beta";

        _page.Accounts[1].TestCommand.Execute(null);

        var info = _started.ShouldHaveSingleItem();
        info.FileName.ShouldBe(@"C:\ff\firefox.exe");
        info.ArgumentList.ShouldBe(["-P", "beta", "-new-tab", "https://github.com/settings/profile"]);
        _page.Accounts[1].Message.ShouldBeNull();
    }

    [Fact]
    public async Task Test_shows_the_line_when_it_cannot_open()
    {
        await _page.RefreshAsync(CancellationToken.None);
        _page.Accounts[0].SelectedId = "firefox|alpha";
        _start = _ => false;

        _page.Accounts[0].TestCommand.Execute(null);

        _page.Accounts[0].Message.ShouldBe("Couldn't open Firefox (alpha). Pick another browser in Settings, GitHub.");
    }

    [Fact]
    public async Task A_gone_browser_stays_selected_and_shows_as_missing()
    {
        _browsers.SetChoice("someone", "chrome|Default");

        await _page.RefreshAsync(CancellationToken.None);

        var row = _page.Accounts[0];
        row.SelectedId.ShouldBe("chrome|Default");
        row.Choices.Single(c => c.Id == "chrome|Default").Label.ShouldBe("Missing: chrome|Default");
    }

    [Fact]
    public async Task No_accounts_says_how_to_log_in()
    {
        _gh.AccountsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>([]));

        await _page.RefreshAsync(CancellationToken.None);

        _page.Accounts.ShouldBeEmpty();
        _page.HasNoAccounts.ShouldBeTrue();
    }

    [Fact]
    public async Task A_refresh_replaces_the_rows_with_the_accounts_there_are_now()
    {
        await _page.RefreshAsync(CancellationToken.None);
        _gh.AccountsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>(["third"]));

        await _page.RefreshAsync(CancellationToken.None);

        _page.Accounts.Select(a => a.Login).ShouldBe(["third"]);
    }

    private sealed class FakeSource : IBrowserSource
    {
        public IReadOnlyList<(string Key, string? Executable)> Installed() => [("Firefox-1A2B", @"C:\ff\firefox.exe")];
        public string? FirefoxProfilesIni() => "[Profile0]\nName=alpha\n[Profile1]\nName=beta\n";
        public string? EdgeLocalState() => null;
        public string? ChromeLocalState() => null;
    }
}
