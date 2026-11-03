using System.Diagnostics;
using CodeSwitchX.Core.GitHub;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Hosting.Browsers;
using CodeSwitchX.Tests;
using CodeSwitchX.UI.GitHub;
using CodeSwitchX.UI.Settings;
using CodeSwitchX.UI.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Settings;

public class GitHubPageViewModelTests
{
    private readonly IGhCli _gh = Substitute.For<IGhCli>();
    private readonly ISettingsStore _store = Substitute.For<ISettingsStore>();
    private readonly List<ProcessStartInfo> _started = [];
    private Func<ProcessStartInfo, bool> _start;
    private readonly FakeSource _source = new();
    private readonly GitHubBrowsers _browsers;
    private readonly GitHubPageViewModel _page;

    public GitHubPageViewModelTests()
    {
        _start = info => { _started.Add(info); return true; };
        _gh.AccountsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GhAccounts.Of(["someone", "other"])));
        var catalog = new BrowserCatalog(_source);
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
    public async Task Picking_another_browser_clears_a_failed_test_line()
    {
        await _page.RefreshAsync(CancellationToken.None);
        _page.Accounts[0].SelectedId = "firefox|alpha";
        _start = _ => false;
        _page.Accounts[0].TestCommand.Execute(null);
        _page.Accounts[0].Message.ShouldNotBeNull();

        _page.Accounts[0].SelectedId = "firefox|beta";

        _page.Accounts[0].Message.ShouldBeNull();
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
        _gh.AccountsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GhAccounts.Of([])));

        await _page.RefreshAsync(CancellationToken.None);

        _page.Accounts.ShouldBeEmpty();
        _page.HasNoAccounts.ShouldBeTrue();
    }

    [Fact]
    public async Task A_refresh_replaces_the_rows_with_the_accounts_there_are_now()
    {
        await _page.RefreshAsync(CancellationToken.None);
        _gh.AccountsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GhAccounts.Of(["third"])));

        await _page.RefreshAsync(CancellationToken.None);

        _page.Accounts.Select(a => a.Login).ShouldBe(["third"]);
    }

    [Fact]
    public async Task A_refresh_keeps_the_row_of_an_account_that_is_still_there_with_its_message()
    {
        await _page.RefreshAsync(CancellationToken.None);
        var kept = _page.Accounts[1];
        kept.SelectedId = "firefox|alpha";
        _start = _ => false;
        kept.TestCommand.Execute(null);
        kept.Message.ShouldNotBeNull();
        _gh.AccountsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GhAccounts.Of(["other", "third"])));

        await _page.RefreshAsync(CancellationToken.None);

        _page.Accounts.Select(a => a.Login).ShouldBe(["other", "third"]);
        _page.Accounts[0].ShouldBeSameAs(kept);
        kept.SelectedId.ShouldBe("firefox|alpha");
        kept.Message.ShouldBe("Couldn't open Firefox (alpha). Pick another browser in Settings, GitHub.");
    }

    [Fact]
    public async Task A_refresh_puts_the_rows_in_the_order_of_the_accounts()
    {
        await _page.RefreshAsync(CancellationToken.None);
        var someone = _page.Accounts[0];
        var other = _page.Accounts[1];
        _gh.AccountsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GhAccounts.Of(["third", "other", "someone"])));

        await _page.RefreshAsync(CancellationToken.None);

        _page.Accounts.Select(a => a.Login).ShouldBe(["third", "other", "someone"]);
        _page.Accounts[1].ShouldBeSameAs(other);
        _page.Accounts[2].ShouldBeSameAs(someone);
    }

    [Fact]
    public async Task A_refresh_gives_a_kept_row_the_browsers_installed_now()
    {
        await _page.RefreshAsync(CancellationToken.None);
        var row = _page.Accounts[0];
        row.SelectedId = "firefox|beta";
        _source.Keys = [("Firefox-1A2B", @"C:\ff\firefox.exe"), ("Some Browser", @"C:\sb\sb.exe")];

        await _page.RefreshAsync(CancellationToken.None);

        _page.Accounts[0].ShouldBeSameAs(row);
        row.Choices.Select(c => c.Id).ShouldBe([BrowserEntry.DefaultId, "firefox|alpha", "firefox|beta", "some browser"]);
        row.SelectedId.ShouldBe("firefox|beta");
    }

    [Fact]
    public async Task A_refresh_leaves_the_choices_of_a_kept_row_alone_when_nothing_changed()
    {
        await _page.RefreshAsync(CancellationToken.None);
        var row = _page.Accounts[0];
        var choices = row.Choices;
        var changes = new List<string?>();
        row.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        await _page.RefreshAsync(CancellationToken.None);

        row.Choices.ShouldBeSameAs(choices);
        changes.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_kept_row_whose_browser_was_uninstalled_shows_it_as_missing()
    {
        await _page.RefreshAsync(CancellationToken.None);
        var row = _page.Accounts[0];
        row.SelectedId = "firefox|beta";
        _source.Keys = [];

        await _page.RefreshAsync(CancellationToken.None);

        row.SelectedId.ShouldBe("firefox|beta");
        row.Choices.Single(c => c.Id == "firefox|beta").Label.ShouldBe("Missing: firefox|beta");
    }

    [Fact]
    public async Task An_account_listed_twice_gets_one_row()
    {
        _gh.AccountsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GhAccounts.Of(["first", "FIRST", "second"])));

        await _page.RefreshAsync(CancellationToken.None);
        await _page.RefreshAsync(CancellationToken.None);

        _page.Accounts.Select(a => a.Login).ShouldBe(["first", "second"]);
    }

    [Fact]
    public async Task A_kept_row_loses_a_failed_test_line_when_its_browser_list_changes()
    {
        await _page.RefreshAsync(CancellationToken.None);
        var row = _page.Accounts[0];
        row.SelectedId = "firefox|alpha";
        _start = _ => false;
        row.TestCommand.Execute(null);
        row.Message.ShouldNotBeNull();
        _source.Keys = [("Firefox-1A2B", @"C:\ff\firefox.exe"), ("Some Browser", @"C:\sb\sb.exe")];

        await _page.RefreshAsync(CancellationToken.None);

        row.Message.ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_read_leaves_the_rows_and_their_lines_alone()
    {
        await _page.RefreshAsync(CancellationToken.None);
        var row = _page.Accounts[0];
        row.SelectedId = "firefox|alpha";
        _start = _ => false;
        row.TestCommand.Execute(null);
        _gh.AccountsAsync(Arg.Any<CancellationToken>()).Returns<Task<GhAccounts>>(_ => throw new InvalidOperationException("boom"));

        await _page.RefreshAsync(CancellationToken.None);

        _page.Accounts.Count.ShouldBe(2);
        _page.Accounts[0].ShouldBeSameAs(row);
        row.Message.ShouldNotBeNull();
        _page.HasNoAccounts.ShouldBeFalse();
    }

    [Fact]
    public async Task An_empty_answer_while_rows_are_shown_means_everyone_logged_out()
    {
        await _page.RefreshAsync(CancellationToken.None);
        _gh.AccountsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GhAccounts.Of([])));

        await _page.RefreshAsync(CancellationToken.None);

        _page.Accounts.ShouldBeEmpty();
        _page.HasNoAccounts.ShouldBeTrue();
    }

    [Fact]
    public async Task A_failed_answer_from_gh_leaves_the_rows_alone()
    {
        await _page.RefreshAsync(CancellationToken.None);
        var row = _page.Accounts[0];
        _gh.AccountsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GhAccounts.Failed()));

        await _page.RefreshAsync(CancellationToken.None);

        _page.Accounts.Count.ShouldBe(2);
        _page.Accounts[0].ShouldBeSameAs(row);
        _page.HasNoAccounts.ShouldBeFalse();
    }

    [Fact]
    public async Task A_failed_answer_from_gh_before_any_row_says_how_to_log_in()
    {
        _gh.AccountsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GhAccounts.Failed()));

        await _page.RefreshAsync(CancellationToken.None);

        _page.HasNoAccounts.ShouldBeTrue();
    }

    private const string InstallGh = "No GitHub accounts found. Install the GitHub command line (gh) and run gh auth login.";

    private const string TooOld = "The GitHub command line (gh) is too old for this. Update it, then reopen this page.";

    private const string CouldNotRead = "Couldn't read the GitHub accounts. Check that the GitHub command line (gh) works, then reopen this page.";

    [Theory]
    [InlineData(GhFailure.NotFound, InstallGh)]
    [InlineData(GhFailure.TooOld, TooOld)]
    [InlineData(GhFailure.Other, CouldNotRead)]
    public async Task With_no_rows_the_page_says_why_gh_could_not_help(GhFailure failure, string expected)
    {
        _gh.AccountsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GhAccounts.Failed(failure)));

        await _page.RefreshAsync(CancellationToken.None);

        _page.HasNoAccounts.ShouldBeTrue();
        _page.NoAccountsText.ShouldBe(expected);
    }

    [Fact]
    public async Task Nobody_logged_in_is_the_install_line()
    {
        _gh.AccountsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GhAccounts.Of([])));

        await _page.RefreshAsync(CancellationToken.None);

        _page.NoAccountsText.ShouldBe(InstallGh);
    }

    [Fact]
    public async Task A_gh_that_throws_is_another_failure()
    {
        _gh.AccountsAsync(Arg.Any<CancellationToken>()).Returns<Task<GhAccounts>>(_ => throw new InvalidOperationException("boom"));

        await _page.RefreshAsync(CancellationToken.None);

        _page.NoAccountsText.ShouldBe(CouldNotRead);
    }

    [Fact]
    public async Task The_line_follows_the_latest_read_and_says_so()
    {
        var changed = new List<string?>();
        _page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _gh.AccountsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GhAccounts.Failed(GhFailure.TooOld)));
        await _page.RefreshAsync(CancellationToken.None);
        _page.NoAccountsText.ShouldBe(TooOld);
        changed.ShouldContain(nameof(GitHubPageViewModel.NoAccountsText));
        _gh.AccountsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GhAccounts.Of([])));

        await _page.RefreshAsync(CancellationToken.None);

        _page.NoAccountsText.ShouldBe(InstallGh);
    }

    [Fact]
    public async Task A_failed_read_is_logged_by_its_kind_only()
    {
        var log = new ListLogger<GitHubPageViewModel>();
        var catalog = new BrowserCatalog(_source);
        var page = new GitHubPageViewModel(_gh, _browsers, catalog, log);
        _gh.AccountsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GhAccounts.Failed(GhFailure.TooOld)));

        await page.RefreshAsync(CancellationToken.None);

        var warning = log.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(Microsoft.Extensions.Logging.LogLevel.Warning);
        warning.Message.ShouldContain("TooOld");
    }

    [Fact]
    public async Task A_failing_browser_read_leaves_the_rows_alone()
    {
        await _page.RefreshAsync(CancellationToken.None);
        _source.Throws = true;

        await _page.RefreshAsync(CancellationToken.None);

        _page.Accounts.Count.ShouldBe(2);
        _page.HasNoAccounts.ShouldBeFalse();
    }

    [Fact]
    public async Task A_failing_gh_read_says_how_to_log_in_instead_of_throwing()
    {
        _gh.AccountsAsync(Arg.Any<CancellationToken>()).Returns<Task<GhAccounts>>(_ => throw new InvalidOperationException("boom"));

        await _page.RefreshAsync(CancellationToken.None);

        _page.HasNoAccounts.ShouldBeTrue();
    }

    [Fact]
    public async Task A_failing_browser_list_still_shows_the_accounts_with_the_Windows_default()
    {
        var source = Substitute.For<IBrowserSource>();
        source.Installed().Returns(_ => throw new InvalidOperationException("boom"));
        var catalog = new BrowserCatalog(source);
        var page = new GitHubPageViewModel(_gh, new GitHubBrowsers(catalog, _store, _ => true, NullLogger<GitHubBrowsers>.Instance), catalog);

        await page.RefreshAsync(CancellationToken.None);

        page.Accounts.Select(a => a.Login).ShouldBe(["someone", "other"]);
        page.Accounts[0].Choices.Select(c => c.Id).ShouldBe([BrowserEntry.DefaultId]);
        page.HasNoAccounts.ShouldBeFalse();
    }

    [Fact]
    public Task The_first_gh_and_the_browser_list_run_off_the_ui_thread()
    {
        int accountsThread = 0, browsersThread = 0;
        var read = new ManualResetEventSlim();
        _gh.AccountsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            accountsThread = Environment.CurrentManagedThreadId;
            return Task.FromResult(GhAccounts.Of([]));
        });
        var source = Substitute.For<IBrowserSource>();
        source.Installed().Returns(_ =>
        {
            browsersThread = Environment.CurrentManagedThreadId;
            read.Set();
            return [];
        });
        var catalog = new BrowserCatalog(source);
        var page = new GitHubPageViewModel(_gh, new GitHubBrowsers(catalog, _store, _ => true, NullLogger<GitHubBrowsers>.Instance), catalog);

        // A thread of its own stands in for the UI thread: a task cannot borrow its id while it waits here.
        return StaThread.RunAsync(() =>
        {
            _ = page.RefreshAsync(CancellationToken.None);

            read.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ShouldBeTrue();
            accountsThread.ShouldNotBe(Environment.CurrentManagedThreadId);
            browsersThread.ShouldNotBe(Environment.CurrentManagedThreadId);
        });
    }

    private sealed class FakeSource : IBrowserSource
    {
        public IReadOnlyList<(string Key, string? Executable)> Keys { get; set; } = [("Firefox-1A2B", @"C:\ff\firefox.exe")];

        public bool Throws { get; set; }

        public IReadOnlyList<(string Key, string? Executable)> Installed() => Throws ? throw new InvalidOperationException("boom") : Keys;
        public string? FirefoxProfilesIni() => "[Profile0]\nName=alpha\n[Profile1]\nName=beta\n";
        public string? ChromiumLocalState(string folder) => null;
    }
}
