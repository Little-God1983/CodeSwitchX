using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using CodeSwitchX.Core.GitHub;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting.Browsers;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.GitHub;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Tests.Infrastructure;
using CodeSwitchX.UI.Yard;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Yard;

[Collection(KeyboardFocusCollection.Name)]
public class GitHubListViewModelTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeGh _gh = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly List<ProcessStartInfo> _started = [];
    private readonly List<GitHubPanelViewModel> _closedLists = [];
    private Func<ProcessStartInfo, bool> _start;

    private sealed class FakeGh : IGhCli
    {
        /// <summary>How many open issues the pill's read counts.</summary>
        public int IssueTotal { get; set; } = 12;

        /// <summary>What the pill's read finds: one issue, <see cref="IssueTotal"/> in all.</summary>
        private string Graphql => GraphqlTemplate.Replace("TOTAL", IssueTotal.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        private const string GraphqlTemplate = """
            {"data":{"repository":{"viewerPermission":"ADMIN",
             "issues":{"totalCount":TOTAL,"nodes":[{"number":1,"title":"First","url":"https://github.com/acme/app/issues/1","updatedAt":"2026-09-23T06:00:00Z","labels":{"nodes":[]}}]},
             "pullRequests":{"totalCount":1,"nodes":[{"number":9,"title":"Ninth","url":"https://github.com/acme/app/pull/9","updatedAt":"2026-09-23T06:00:00Z","headRefName":"b","author":{"login":"me"},"labels":{"nodes":[]}}]}}}}
            """;

        /// <summary>The next issue list read answers with this; null fails it. Awaited, so a test can hold the answer back.</summary>
        public TaskCompletionSource<string?> List { get; } = new();

        /// <summary>The same for the pull request list, which stays unanswered unless a test answers it.</summary>
        public TaskCompletionSource<string?> PullRequestList { get; } = new();

        public Task<GhAccounts> AccountsAsync(CancellationToken ct) => Task.FromResult(GhAccounts.Of(["me"]));

        /// <summary>Set when a list read reaches gh, after <see cref="ListThread"/>.</summary>
        public ManualResetEventSlim ListAsked { get; } = new();

        public int ListThread { get; private set; }

        /// <summary>The kind of each list read, in the order they reached gh: "issue" or "pr".</summary>
        public List<string> Asked { get; } = [];

        /// <summary>The token the last list read was given.</summary>
        public CancellationToken ListToken { get; private set; }

        public async Task<GhAnswer> AnswerAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct) =>
            new(await RunAsAsync(login, arguments, ct));

        public async Task<GhAnswer> ListAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct) =>
            new(await RunAsAsync(login, arguments, ct));

        public Task<string?> RunAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            if (arguments[0] == "api")
            {
                return Task.FromResult<string?>(Graphql);
            }

            lock (Asked)
            {
                Asked.Add(arguments[0]);
            }

            ListThread = Environment.CurrentManagedThreadId;
            ListToken = ct;
            ListAsked.Set();
            return arguments[0] == "pr" ? PullRequestList.Task : List.Task;
        }
    }

    public GitHubListViewModelTests()
    {
        _start = info => { _started.Add(info); return true; };
    }

    private static string ListOf(int count) => "[" + string.Join(",", Enumerable.Range(1, count).Select(n =>
        $$"""{"number":{{n}},"title":"Item {{n}}","url":"https://github.com/acme/app/issues/{{n}}","updatedAt":"2026-09-23T06:00:00Z","labels":[]}""")) + "]";

    private static string PullRequestsOf(int count) => "[" + string.Join(",", Enumerable.Range(1, count).Select(n =>
        $$$"""{"number":{{{n}}},"title":"Pull {{{n}}}","url":"https://github.com/acme/app/pull/{{{n}}}","updatedAt":"2026-09-23T06:00:00Z","labels":[],"headRefName":"b{{{n}}}","author":{"login":"me"}}""")) + "]";

    private static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(until, "the condition never came true");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private GitHubBrowsers Browsers(Func<ProcessStartInfo, bool>? start = null) =>
        new(new BrowserCatalog(new FakeSource()), Substitute.For<ISettingsStore>(), start ?? (info => _start(info)), NullLogger<GitHubBrowsers>.Instance);

    /// <summary>An activity that has read the repository (12 issues, 1 pull request) and the pill of one kind of it.</summary>
    private async Task<(GitHubActivity Activity, GitHubPill Pill)> PillAsync(GitHubItemKind kind = GitHubItemKind.Issue)
    {
        var activity = new GitHubActivity(_gh, new GitHubReader(_gh), (_, _) => Task.FromResult(new OriginLookup("https://github.com/acme/app.git")),
            NullLogger<GitHubActivity>.Instance);
        await activity.RefreshAsync(["app"], TestContext.Current.CancellationToken);
        return (activity, new GitHubPill(activity.ForFolder("app")!, kind));
    }

    private GitHubPanelViewModel Open(GitHubActivity activity, GitHubPill pill, GitHubBrowsers? browsers = null) =>
        new(pill.Activity, pill.Kind, new GitHubReader(_gh), activity, browsers ?? Browsers(), _clipboard, new ImmediateDispatcher(), _time, NullLogger.Instance, _closedLists.Add);

    [Fact]
    public async Task Opening_shows_last_known_rows_at_once_then_the_fresh_list()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;

        list.Rows.Select(r => r.NumberText).ShouldBe(["#1"]);

        var load = list.LoadAsync(TestContext.Current.CancellationToken);
        list.Rows.Count.ShouldBe(1);
        _gh.List.SetResult(ListOf(3));
        await load;

        list.Rows.Select(r => r.NumberText).ShouldBe(["#1", "#2", "#3"]);
        list.Rows[0].AgeText.ShouldBe("updated today");
        list.LoadMessage.ShouldBeNull();
    }

    [Fact]
    public async Task Heading_names_the_repo()
    {
        var (activity, pill) = await PillAsync();

        Open(activity, pill).Heading.ShouldBe("app");
    }

    [Fact]
    public async Task A_pill_opens_its_own_tab()
    {
        var (activity, pill) = await PillAsync();

        var model = Open(activity, new GitHubPill(pill.Activity, GitHubItemKind.PullRequest));

        model.Shown.ShouldBeSameAs(model.PullRequests);
        model.IsPullRequestsShown.ShouldBeTrue();
        Open(activity, pill).Shown.Kind.ShouldBe(GitHubItemKind.Issue);
    }

    [Fact]
    public async Task The_default_tab_is_pull_requests_when_any_are_open_else_issues()
    {
        var (_, pill) = await PillAsync();

        GitHubPanelViewModel.DefaultKind(pill.Activity).ShouldBe(GitHubItemKind.PullRequest);
        GitHubPanelViewModel.DefaultKind(pill.Activity with { OpenPullRequests = 0 }).ShouldBe(GitHubItemKind.Issue);
    }

    [Fact]
    public async Task Tabs_name_their_kind_and_count()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        model.PullRequests.TabText.ShouldBe("Pull requests 1");
        model.Issues.TabText.ShouldBe("Issues 12");
        _gh.List.SetResult(ListOf(19));

        await model.Issues.LoadAsync(TestContext.Current.CancellationToken);

        model.Issues.TabText.ShouldBe("Issues 19");
    }

    [Fact]
    public async Task Show_switches_the_shown_list()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, new GitHubPill(pill.Activity, GitHubItemKind.PullRequest));

        model.ShowCommand.Execute(GitHubItemKind.Issue);

        model.Shown.ShouldBeSameAs(model.Issues);
        model.IsPullRequestsShown.ShouldBeFalse();
    }

    [Fact]
    public async Task The_shown_kind_is_read_first_then_the_other()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var load = model.LoadAsync(TestContext.Current.CancellationToken);
        _gh.Asked.ShouldBe(["issue"], "the other list waits for the shown one");

        _gh.List.SetResult(ListOf(3));
        _gh.PullRequestList.SetResult(PullRequestsOf(2));
        await load;

        _gh.Asked.ShouldBe(["issue", "pr"]);
    }

    [Fact]
    public async Task Both_reads_update_the_tile()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        _gh.List.SetResult(ListOf(4));
        _gh.PullRequestList.SetResult(PullRequestsOf(2));

        await model.LoadAsync(TestContext.Current.CancellationToken);

        activity.ForFolder("app")!.OpenIssues.ShouldBe(4);
        activity.ForFolder("app")!.OpenPullRequests.ShouldBe(2);
        model.PullRequests.TabText.ShouldBe("Pull requests 2");
    }

    [Fact]
    public async Task A_failed_second_read_only_marks_its_own_tab()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        _gh.List.SetResult(ListOf(3));
        _gh.PullRequestList.SetResult(null);

        await model.LoadAsync(TestContext.Current.CancellationToken);

        model.Issues.LoadMessage.ShouldBeNull();
        model.PullRequests.LoadMessage.ShouldBe("Couldn't load the latest list. Showing what was last read.");
    }

    [Fact]
    public async Task Closing_during_the_second_read_cancels_it_and_changes_nothing()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var load = model.LoadAsync(TestContext.Current.CancellationToken);
        _gh.List.SetResult(ListOf(3));
        await Eventually(() => _gh.Asked.Count == 2);

        model.CloseCommand.Execute(null);
        _gh.ListToken.IsCancellationRequested.ShouldBeTrue();
        _gh.PullRequestList.SetResult(PullRequestsOf(5)); // an answer that was already on its way
        await load;

        model.PullRequests.Rows.Count.ShouldBe(1);
        activity.ForFolder("app")!.OpenPullRequests.ShouldBe(1);
    }

    [Fact]
    public async Task The_arrow_opens_all_pull_requests_or_issues_on_github()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);

        model.OpenOnGitHubCommand.Execute(GitHubItemKind.PullRequest);
        model.OpenOnGitHubCommand.Execute(GitHubItemKind.Issue);

        _started.Select(s => s.FileName).ShouldBe(["https://github.com/acme/app/pulls", "https://github.com/acme/app/issues"]);
        model.OpenMessage.ShouldBeNull();
        _closedLists.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_arrow_the_browser_cannot_open_shows_its_line()
    {
        var (activity, pill) = await PillAsync();
        var browsers = Browsers();
        browsers.SetChoice("me", "firefox|alpha");
        _start = _ => false;
        var model = Open(activity, pill, browsers);

        model.OpenOnGitHubCommand.Execute(GitHubItemKind.PullRequest);

        model.OpenMessage.ShouldBe("Couldn't open Firefox (alpha). Pick another browser in Settings, GitHub.");
    }

    [Fact]
    public async Task An_empty_tab_says_so_once_loaded()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, new GitHubPill(pill.Activity, GitHubItemKind.PullRequest));
        var load = model.PullRequests.LoadAsync(TestContext.Current.CancellationToken);
        model.PullRequests.EmptyText.ShouldBeNull("still loading");

        _gh.PullRequestList.SetResult("[]");
        await load;

        model.PullRequests.EmptyText.ShouldBe("No open pull requests.");
        model.Issues.EmptyText.ShouldBeNull("it has rows");
    }

    [Fact]
    public async Task Copy_all_links_copies_the_shown_tab()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);

        model.ShowCommand.Execute(GitHubItemKind.PullRequest);
        model.CopyAllLinksCommand.Execute(null);

        _clipboard.Text.ShouldBe("https://github.com/acme/app/pull/9");
    }

    [Fact]
    public async Task The_tabs_and_arrows_are_tab_stops_in_order()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);

        await StaThread.RunAsync(() =>
        {
            var names = TabStops(PanelView(model).Children.OfType<Border>().Single()).Select(AutomationProperties.GetName).ToList();

            names.Take(7).ShouldBe(["Close the list", "Open acme/app on GitHub", "Show pull requests", "Open all pull requests on GitHub", "Show issues",
                "Open all issues on GitHub", "Copy all links"]);
            names.Skip(7).ShouldBe(["Open #1"]);
        });
    }

    [Fact]
    public async Task AccountLine_names_account_and_browser()
    {
        var (activity, pill) = await PillAsync();
        var browsers = Browsers();
        browsers.SetChoice("me", "firefox|alpha");

        Open(activity, pill, browsers).AccountLine.ShouldBe("Read with me. Opens in Firefox (alpha).");
    }

    [Fact]
    public async Task A_fresh_list_updates_the_tile_count()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        _gh.List.SetResult(ListOf(4));

        await list.LoadAsync(TestContext.Current.CancellationToken);

        activity.ForFolder("app")!.OpenIssues.ShouldBe(4);
    }

    [Fact]
    public async Task A_failed_load_keeps_the_rows_and_says_so()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        _gh.List.SetResult(null);

        await list.LoadAsync(TestContext.Current.CancellationToken);

        list.LoadMessage.ShouldBe("Couldn't load the latest list. Showing what was last read.");
        list.Rows.Select(r => r.NumberText).ShouldBe(["#1"]);
        activity.ForFolder("app")!.OpenIssues.ShouldBe(12);
    }

    [Fact]
    public async Task Clicking_a_row_opens_it_and_keeps_the_panel_open()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;

        model.OpenRowCommand.Execute(list.Rows[0]);

        _started.ShouldHaveSingleItem().FileName.ShouldBe("https://github.com/acme/app/issues/1");
        model.OpenMessage.ShouldBeNull();
        _closedLists.ShouldBeEmpty();
    }

    [Fact]
    public async Task Clicking_the_heading_opens_the_repository_and_keeps_the_panel_open()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;

        await StaThread.RunAsync(() =>
        {
            var heading = Descendants(PanelView(model)).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Open acme/app on GitHub");

            heading.Command.Execute(heading.CommandParameter);
        });

        _started.ShouldHaveSingleItem().FileName.ShouldBe("https://github.com/acme/app");
        model.OpenMessage.ShouldBeNull();
        _closedLists.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_browser_that_cannot_open_the_repository_shows_its_line_in_the_panel()
    {
        var (activity, pill) = await PillAsync();
        var browsers = Browsers();
        browsers.SetChoice("me", "firefox|alpha");
        _start = _ => false;
        var model = Open(activity, pill, browsers);
        var list = model.Shown;

        model.OpenRepositoryCommand.Execute(null);

        model.OpenMessage.ShouldBe("Couldn't open Firefox (alpha). Pick another browser in Settings, GitHub.");
        _closedLists.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_close_button_stays_the_panel_s_first_stop()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;

        await StaThread.RunAsync(() =>
        {
            var first = TabStops(PanelView(model))[0].ShouldBeOfType<Button>();

            AutomationProperties.GetName(first).ShouldBe("Close the list");
        });
    }

    [Fact]
    public async Task A_browser_that_cannot_open_shows_its_line_in_the_panel()
    {
        var (activity, pill) = await PillAsync();
        var browsers = Browsers();
        browsers.SetChoice("me", "firefox|alpha");
        _start = _ => false;
        var model = Open(activity, pill, browsers);
        var list = model.Shown;

        model.OpenRowCommand.Execute(list.Rows[0]);

        model.OpenMessage.ShouldBe("Couldn't open Firefox (alpha). Pick another browser in Settings, GitHub.");
        _closedLists.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_row_that_opens_clears_an_earlier_open_failure()
    {
        var (activity, pill) = await PillAsync();
        var browsers = Browsers();
        browsers.SetChoice("me", "firefox|alpha");
        var model = Open(activity, pill, browsers);
        var list = model.Shown;
        _start = _ => false;
        model.OpenRowCommand.Execute(list.Rows[0]);
        model.OpenMessage.ShouldNotBeNull();

        _start = info => { _started.Add(info); return true; };
        model.OpenRowCommand.Execute(list.Rows[0]);

        model.OpenMessage.ShouldBeNull();
    }

    [Fact]
    public async Task A_load_failure_and_an_open_failure_each_keep_their_own_line()
    {
        var (activity, pill) = await PillAsync();
        var browsers = Browsers();
        browsers.SetChoice("me", "firefox|alpha");
        var model = Open(activity, pill, browsers);
        var list = model.Shown;
        _gh.List.SetResult(null);
        _start = _ => false;

        model.OpenRowCommand.Execute(list.Rows[0]);
        await list.LoadAsync(TestContext.Current.CancellationToken);

        model.OpenMessage.ShouldStartWith("Couldn't open");
        list.LoadMessage.ShouldBe("Couldn't load the latest list. Showing what was last read.");
    }

    [Fact]
    public async Task A_load_that_works_clears_the_load_line_and_leaves_the_open_line()
    {
        var (activity, pill) = await PillAsync();
        var browsers = Browsers();
        browsers.SetChoice("me", "firefox|alpha");
        var model = Open(activity, pill, browsers);
        var list = model.Shown;
        _start = _ => false;
        model.OpenRowCommand.Execute(list.Rows[0]);
        _gh.List.SetResult(ListOf(3));

        await list.LoadAsync(TestContext.Current.CancellationToken);

        list.LoadMessage.ShouldBeNull();
        model.OpenMessage.ShouldStartWith("Couldn't open");
    }

    [Fact]
    public async Task Closing_while_loading_cancels_the_read_and_changes_nothing()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        var load = list.LoadAsync(TestContext.Current.CancellationToken);
        _gh.ListToken.IsCancellationRequested.ShouldBeFalse();

        model.CloseCommand.Execute(null);
        _gh.ListToken.IsCancellationRequested.ShouldBeTrue("the gh run is not left to run out its timeout");
        _gh.List.SetResult(ListOf(3)); // an answer that was already on its way
        await load;

        _closedLists.ShouldBe([model]);
        list.Rows.Count.ShouldBe(1);
        list.TabText.ShouldBe("Issues 12");
        activity.ForFolder("app")!.OpenIssues.ShouldBe(12, "a late result after the close does not exist");
    }

    [Fact]
    public async Task Opening_another_pill_cancels_the_read_of_the_list_it_replaces()
    {
        var (activity, pill) = await PillAsync();
        var yard = NewYard(activity);
        yard.OpenGitHubListCommand.Execute(pill);
        var first = yard.GitHubPanel.ShouldNotBeNull();
        _gh.ListAsked.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ShouldBeTrue();
        var token = _gh.ListToken;

        yard.OpenGitHubListCommand.Execute(new GitHubPill(pill.Activity, GitHubItemKind.PullRequest));

        token.IsCancellationRequested.ShouldBeTrue();
        _gh.List.SetResult(ListOf(3));
        await first.Loaded;
    }

    [Fact]
    public async Task The_panel_says_it_is_loading_until_the_read_ends_however_it_ends()
    {
        foreach (var end in new Action<FakeGh>[] { g => g.List.SetResult(ListOf(3)), g => g.List.SetResult(null), g => g.List.SetException(new InvalidOperationException("boom")) })
        {
            var gh = new FakeGh();
            var activity = new GitHubActivity(gh, new GitHubReader(gh), (_, _) => Task.FromResult(new OriginLookup("https://github.com/acme/app.git")), NullLogger<GitHubActivity>.Instance);
            await activity.RefreshAsync(["app"], TestContext.Current.CancellationToken);
            var list = new GitHubPanelViewModel(activity.ForFolder("app")!, GitHubItemKind.Issue, new GitHubReader(gh), activity, Browsers(),
                _clipboard, new ImmediateDispatcher(), _time, NullLogger.Instance, _closedLists.Add).Shown;
            list.LoadingMessage.ShouldBe("Loading the full list...");

            var load = list.LoadAsync(TestContext.Current.CancellationToken);
            list.LoadingMessage.ShouldBe("Loading the full list...");
            end(gh);
            await load;

            list.LoadingMessage.ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_cancelled_read_stops_saying_it_is_loading()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        using var stop = new CancellationTokenSource();
        var load = list.LoadAsync(stop.Token);

        stop.Cancel();
        _gh.List.SetCanceled(stop.Token);
        await load;

        list.LoadingMessage.ShouldBeNull();
    }

    [Fact]
    public async Task A_list_that_a_newer_result_overtook_shows_its_own_total_and_the_tile_keeps_the_newer_one()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        var load = list.LoadAsync(TestContext.Current.CancellationToken);
        _gh.IssueTotal = 20;
        await activity.RefreshAsync(["app"], TestContext.Current.CancellationToken); // a round stores a newer result while the list is read
        _gh.List.SetResult(ListOf(3));
        await load;

        list.Rows.Select(r => r.NumberText).ShouldBe(["#1", "#2", "#3"]);
        list.TabText.ShouldBe("Issues 3");
        activity.ForFolder("app")!.OpenIssues.ShouldBe(20);
        activity.ForFolder("app")!.NewestIssues.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_cancelled_load_changes_nothing_and_throws_nothing()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        using var stop = new CancellationTokenSource();
        var load = list.LoadAsync(stop.Token);

        stop.Cancel();
        _gh.List.SetCanceled(stop.Token);
        await load;

        list.Rows.Count.ShouldBe(1);
        list.LoadMessage.ShouldBeNull();
        activity.ForFolder("app")!.OpenIssues.ShouldBe(12);
    }

    [Fact]
    public async Task A_reader_that_throws_shows_the_failed_load_line()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        _gh.List.SetException(new InvalidOperationException("boom"));

        await list.LoadAsync(TestContext.Current.CancellationToken);

        list.LoadMessage.ShouldBe("Couldn't load the latest list. Showing what was last read.");
        list.Rows.Select(r => r.NumberText).ShouldBe(["#1"]);
    }

    [Fact]
    public async Task Opening_another_pill_replaces_the_list()
    {
        var (activity, pill) = await PillAsync();
        var yard = NewYard(activity);
        yard.OpenGitHubListCommand.Execute(pill);
        var first = yard.GitHubPanel;
        first.ShouldNotBeNull();

        yard.OpenGitHubListCommand.Execute(new GitHubPill(pill.Activity, GitHubItemKind.PullRequest));

        var second = yard.GitHubPanel;
        second.ShouldNotBeNull().ShouldNotBeSameAs(first);
        second.Shown.TabText.ShouldBe("Pull requests 1");
        // The earlier load finishes now and finds its panel gone.
        _gh.List.SetResult(ListOf(3));
        await first.Loaded;
        yard.GitHubPanel.ShouldBeSameAs(second);
        first.Shown.Rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Disposing_the_yard_closes_an_open_list()
    {
        var (activity, pill) = await PillAsync();
        var yard = NewYard(activity);
        yard.OpenGitHubListCommand.Execute(pill);
        var list = yard.GitHubPanel.ShouldNotBeNull();

        yard.Dispose();
        _gh.List.SetResult(ListOf(3));
        await list.Loaded;

        yard.GitHubPanel.ShouldBeNull();
        list.Shown.Rows.Count.ShouldBe(1, "a load that finishes later leaves the closed list alone");
    }

    [Fact]
    public async Task Without_the_readers_a_click_does_nothing()
    {
        var (_, pill) = await PillAsync();
        var yard = NewYard(null);

        yard.OpenGitHubListCommand.Execute(pill);

        yard.GitHubPanel.ShouldBeNull();
    }

    [Fact]
    public async Task Opening_a_list_starts_gh_off_the_ui_thread()
    {
        var (activity, pill) = await PillAsync();

        // A thread of its own stands in for the UI thread: a task cannot borrow its id while it waits here.
        await StaThread.RunAsync(() =>
        {
            NewYard(activity).OpenGitHubListCommand.Execute(pill);

            _gh.ListAsked.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ShouldBeTrue();
            _gh.ListThread.ShouldNotBe(Environment.CurrentManagedThreadId);
        });
    }

    [Fact]
    public async Task Escape_on_the_yard_closes_an_open_list_and_is_left_alone_otherwise()
    {
        var (activity, pill) = await PillAsync();

        await StaThread.RunAsync(() =>
        {
            var yard = NewYard(activity);
            var bindings = YardViewXaml().Element(Wpf + "UserControl.InputBindings").ShouldNotBeNull();
            var view = (UserControl)XamlReader.Parse(new XElement(Wpf + "UserControl", bindings).ToString());
            view.DataContext = yard;
            // The binding attaches to its context in a task of the dispatcher's, as it would when the window shows.
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            using var source = new HwndSource(new HwndSourceParameters("test") { WindowStyle = 0 });

            bool Escape()
            {
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent };
                view.RaiseEvent(key);
                return key.Handled;
            }

            Escape().ShouldBeFalse("no list open: the key belongs to whoever else wants it");

            yard.OpenGitHubListCommand.Execute(pill);
            yard.GitHubPanel.ShouldNotBeNull();

            Escape().ShouldBeTrue();
            yard.GitHubPanel.ShouldBeNull();
            Escape().ShouldBeFalse("closed again");
        });
    }

    [Fact]
    public void Tab_stays_inside_the_panel()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var panel = YardViewXaml().Descendants(Wpf + "Border").Single(b => (string?)b.Attribute(x + "Name") == "GitHubPanel");

        // Tab, Ctrl+Tab and the arrow keys: none may reach the controls under the shade
        ((string?)panel.Attribute("KeyboardNavigation.TabNavigation")).ShouldBe("Cycle");
        ((string?)panel.Attribute("KeyboardNavigation.ControlTabNavigation")).ShouldBe("Cycle");
        ((string?)panel.Attribute("KeyboardNavigation.DirectionalNavigation")).ShouldBe("Cycle");
    }

    [Fact]
    public async Task The_top_bar_has_the_refresh_button_and_shows_the_failure_line_only_while_there_is_one()
    {
        var (activity, _) = await PillAsync();

        await StaThread.RunAsync(() =>
        {
            var yard = NewYard(activity);
            var bar = TopBar(yard);

            var refresh = Descendants(bar).OfType<Button>().Single(b => Equals(b.Content, "Refresh"));
            refresh.Command.ShouldBeSameAs(yard.RefreshCommand);
            const string Line = "Couldn't read GitHub.";
            bar.UpdateLayout();
            Descendants(bar).OfType<TextBlock>().ShouldNotContain(t => t.Text == Line);

            yard.RefreshMessage = Line;
            bar.UpdateLayout();

            Descendants(bar).OfType<TextBlock>().Single(t => t.Text == Line).Visibility.ShouldBe(Visibility.Visible);
        });
    }

    [Fact]
    public async Task A_click_on_the_failure_line_puts_it_away_and_reaches_nothing_under_it()
    {
        var (activity, _) = await PillAsync();

        await StaThread.RunAsync(() =>
        {
            var yard = NewYard(activity);
            var bar = TopBar(yard);
            const string Line = "Couldn't read GitHub.";
            yard.RefreshMessage = Line;
            bar.UpdateLayout();

            var line = Descendants(bar).OfType<Button>().Single(b => AutomationProperties.GetName(b) == Line);
            line.IsHitTestVisible.ShouldBeTrue("a click through it would land on a tile it hides");
            line.Command.Execute(line.CommandParameter);

            yard.RefreshMessage.ShouldBeNull();
        });
    }

    [Fact]
    public async Task The_failure_line_never_takes_the_keyboard()
    {
        var (activity, _) = await PillAsync();

        await StaThread.RunAsync(() =>
        {
            var yard = NewYard(activity);
            var bar = TopBar(yard);
            const string Line = "Couldn't read GitHub.";
            yard.RefreshMessage = Line;
            bar.UpdateLayout();

            // It collapses when clicked: with the keyboard on it, the keyboard would fall to the window. And it comes first in the bar's markup, so it would be the first Tab stop.
            TabStops(bar).ShouldNotContain(stop => AutomationProperties.GetName(stop) == Line);
        });
    }

    [Fact]
    public async Task A_wrapped_bar_keeps_each_size_control_together()
    {
        var (activity, _) = await PillAsync();

        await StaThread.RunAsync(() =>
        {
            for (var width = 300; width <= 900; width += 20)
            {
                var bar = TopBar(NewYard(activity), width);
                var texts = Descendants(bar).OfType<TextBlock>().ToList();
                var percents = texts.Where(t => t.Text.Contains('%', StringComparison.Ordinal)).ToList();
                foreach (var (name, percent) in new[] { ("Tile size", percents[0]), ("Font size", percents[1]) })
                {
                    var label = texts.Single(t => t.Text == name);
                    var slider = Descendants(bar).OfType<Slider>().Single(s => AutomationProperties.GetName(s) == name);

                    double Middle(FrameworkElement e) => e.TranslatePoint(new Point(0, e.ActualHeight / 2), bar).Y;
                    Middle(label).ShouldBe(Middle(slider), 1, $"{name}: its label beside its slider at width {width}");
                    Middle(percent).ShouldBe(Middle(slider), 1, $"{name}: its percentage beside its slider at width {width}");
                }
            }
        });
    }

    [Fact]
    public async Task The_failure_line_has_room_when_the_controls_fill_the_bar()
    {
        var (activity, _) = await PillAsync();

        await StaThread.RunAsync(() =>
        {
            var yard = NewYard(activity);
            var bar = TopBar(yard, width: 1000);
            const string Line = "Couldn't read GitHub. The pills show what was last read.";

            yard.RefreshMessage = Line;
            bar.UpdateLayout();

            // The sentence runs to well over 300 units at the bar's font; the controls beside it leave less than 100.
            Descendants(bar).OfType<TextBlock>().Single(t => t.Text == Line).ActualWidth.ShouldBeGreaterThan(250);
        });
    }

    [Fact]
    public async Task A_failure_line_that_comes_late_does_not_move_the_board()
    {
        var (activity, _) = await PillAsync();

        await StaThread.RunAsync(() =>
        {
            var yard = NewYard(activity);
            var bar = TopBar(yard);
            var border = bar.Children.OfType<Border>().Single();
            var height = border.DesiredSize.Height;
            height.ShouldBeGreaterThan(0);

            yard.RefreshMessage = "Couldn't read GitHub.";
            bar.UpdateLayout();

            border.DesiredSize.Height.ShouldBe(height, "the board sits under the bar, so any change moves every tile");
        });
    }

    [Theory]
    [InlineData(900)] // the window's MinWidth
    [InlineData(700)]
    public async Task A_bar_too_narrow_for_one_row_keeps_the_title_and_every_control_in_view(double width)
    {
        var (activity, _) = await PillAsync();

        await StaThread.RunAsync(() =>
        {
            var bar = TopBar(NewYard(activity), width);
            var settings = Descendants(bar).OfType<Button>().Single(b => Equals(b.Content, "Settings"));
            var title = Descendants(bar).OfType<TextBlock>().Single(t => t.Text == "Yard");

            settings.TranslatePoint(new Point(settings.ActualWidth, 0), bar).X.ShouldBeLessThanOrEqualTo(width);
            title.RenderSize.Width.ShouldBe(title.DesiredSize.Width, 0.5, "the title is not cut");
        });
    }

    [Fact]
    public async Task The_refresh_button_looks_off_while_it_runs()
    {
        var (activity, _) = await PillAsync();

        await StaThread.RunAsync(() =>
        {
            var yard = NewYard(activity);
            var bar = TopBar(yard);
            var refresh = Descendants(bar).OfType<Button>().Single(b => Equals(b.Content, "Refresh"));
            refresh.Opacity.ShouldBe(1);

            yard.IsRefreshing = true;

            refresh.Opacity.ShouldBeLessThan(1);
        });
    }

    /// <summary>The Yard's top bar as it shows it: its markup from the file, on the application's resources.</summary>
    private static Grid TopBar(YardViewModel yard, double width = 1600)
    {
        var root = YardViewXaml();
        var markup = new XElement(root.Elements(Wpf + "Grid").Single().Elements(Wpf + "DockPanel").Single().Elements(Wpf + "Border").First());
        var grid = new XElement(Wpf + "Grid", root.Attributes().Where(a => a.IsNamespaceDeclaration),
            new XElement(Wpf + "Grid.Resources", GitPillsXamlTests.AppResourcesXaml()), markup);
        var bar = (Grid)XamlReader.Parse(GitPillsXamlTests.AssemblyQualified(grid.ToString()));
        bar.DataContext = yard;
        bar.Width = width;
        bar.Height = 120;
        bar.Measure(new Size(width, 120));
        bar.Arrange(new Rect(0, 0, width, 120));
        bar.UpdateLayout();
        return bar;
    }

    /// <summary>
    /// The list panel as the Yard shows it: its markup from the file, on the application's resources, with the list as the context
    /// of the Yard.
    /// </summary>
    private static Grid PanelView(GitHubPanelViewModel? model)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var root = YardViewXaml();
        var markup = new XElement(root.Descendants(Wpf + "Border").Single(b => (string?)b.Attribute(x + "Name") == "GitHubPanel"));
        // in a Grid of its own: the panel's attributes look their resources up outside it
        var grid = new XElement(Wpf + "Grid", root.Attributes().Where(a => a.IsNamespaceDeclaration),
            new XElement(Wpf + "Grid.Resources", GitPillsXamlTests.AppResourcesXaml()), markup);
        var panel = (Grid)XamlReader.Parse(GitPillsXamlTests.AssemblyQualified(grid.ToString()));
        panel.Children.OfType<Border>().Single().DataContext = new { GitHubPanel = model };
        // a size of its own: in a window that sizes to its content, a list of a thousand rows would be as tall as all of them
        panel.Width = 560;
        panel.Height = 600;
        panel.Measure(new Size(560, 600));
        panel.Arrange(new Rect(0, 0, 560, 600));
        panel.UpdateLayout();
        return panel;
    }

    /// <summary>What Tab can land on, in the order of the tree: focusable, a tab stop, enabled, and not collapsed by the markup.</summary>
    private static List<UIElement> TabStops(DependencyObject root)
    {
        var found = new List<UIElement>();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { Visibility: not Visibility.Visible })
            {
                continue;
            }

            if (child is UIElement { IsEnabled: true, Focusable: true } element && KeyboardNavigation.GetIsTabStop(element))
            {
                found.Add(element);
            }

            found.AddRange(TabStops(child));
        }

        return found;
    }

    [Fact]
    public async Task Each_row_is_one_tab_stop()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        _gh.List.SetResult(ListOf(3));
        await list.LoadAsync(TestContext.Current.CancellationToken);

        await StaThread.RunAsync(() =>
        {
            var stops = TabStops(PanelView(model).Children.OfType<Border>().Single());

            // the close button, the heading, each tab and its arrow, copy all, then one button a row
            stops.Select(s => s.GetType().Name).ShouldBe(Enumerable.Repeat("Button", 10).ToList());
            stops.Skip(7).Select(s => ((FrameworkElement)s).DataContext).ShouldBe(list.Rows);
        });
    }

    [Fact]
    public async Task The_panel_shows_each_failure_line_only_while_it_has_something_to_say()
    {
        var (activity, pill) = await PillAsync();
        var browsers = Browsers();
        browsers.SetChoice("me", "firefox|alpha");
        var model = Open(activity, pill, browsers);
        var list = model.Shown;
        _start = _ => false;

        await StaThread.RunAsync(() =>
        {
            var panel = PanelView(model);
            // the failure lines are the amber ones
            List<TextBlock> Lines() => Descendants(panel).OfType<TextBlock>().Where(t => t.Visibility == Visibility.Visible && t.Foreground is SolidColorBrush b && b.Color == Color.FromRgb(0xF0, 0xC6, 0x74)).ToList();

            Lines().ShouldBe([], "nothing to say, nothing shown");

            model.OpenRowCommand.Execute(list.Rows[0]);
            panel.UpdateLayout();

            var shown = Lines();
            shown.ShouldHaveSingleItem().Text.ShouldStartWith("Couldn't open");
        });
    }

    /// <summary>The panel as the Yard has it, with its focus handling, under a window of its own, and the keyboard on the first row.</summary>
    private static (Grid Grid, Border Panel, HwndSource Source) FocusedPanel(GitHubPanelViewModel model)
    {
        var grid = PanelView(model);
        var source = new HwndSource(new HwndSourceParameters("test") { WindowStyle = 0 }) { RootVisual = grid };
        var panel = grid.Children.OfType<Border>().Single();
        _ = new GitHubPanelFocus(grid, panel, () => { });
        Keyboard.Focus(RowButtons(panel).First());
        return (grid, panel, source);
    }

    [Fact]
    public async Task A_panel_that_appears_puts_the_keyboard_on_a_stop_inside_it_and_Shift_Tab_stays_there()
    {
        var model = await ListWithRowsAsync(40);
        var list = model.Shown;

        await StaThread.RunAsync(() =>
        {
            var grid = PanelView(null);
            using var source = new HwndSource(new HwndSourceParameters("test") { WindowStyle = 0 }) { RootVisual = grid };
            var panel = grid.Children.OfType<Border>().Single();
            _ = new GitHubPanelFocus(grid, panel, () => { });
            var outside = new Button();
            grid.Children.Add(outside); // what the keyboard could fall to when it leaves the panel

            panel.DataContext = new { GitHubPanel = model }; // the list opens: the panel appears
            panel.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            Keyboard.FocusedElement.ShouldNotBeSameAs(panel);
            for (var step = 0; step < 6; step++)
            {
                var focused = (DependencyObject)Keyboard.FocusedElement;
                focused.ShouldBeOfType<Button>();
                panel.IsAncestorOf(focused).ShouldBeTrue($"step {step}");
                ((UIElement)focused).MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
                panel.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }
        });
    }

    [Fact]
    public async Task A_swap_of_the_rows_keeps_the_keyboard_in_the_panel()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        _gh.List.SetResult(ListOf(3));
        await list.LoadAsync(TestContext.Current.CancellationToken);

        await StaThread.RunAsync(() =>
        {
            var (_, panel, source) = FocusedPanel(model);
            using var _ = source;
            panel.IsKeyboardFocusWithin.ShouldBeTrue();

            list.Rows = list.Rows.Take(2).ToList(); // the focused button is rebuilt
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            panel.IsKeyboardFocusWithin.ShouldBeTrue();
            Keyboard.FocusedElement.ShouldBeOfType<Button>().ShouldNotBeNull(); // a stop in the panel, not the panel itself
        });
    }

    [Fact]
    public async Task A_keyboard_that_is_nowhere_without_a_swap_is_not_pulled_back()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        _gh.List.SetResult(ListOf(3));
        await list.LoadAsync(TestContext.Current.CancellationToken);

        await StaThread.RunAsync(() =>
        {
            var (_, _, source) = FocusedPanel(model);
            using var _ = source;

            Keyboard.ClearFocus(); // as the app losing the activation leaves it
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            Keyboard.FocusedElement.ShouldBeNull();
        });
    }

    [Fact]
    public async Task A_list_at_the_limit_keeps_the_graphql_total_and_says_it_is_cut_short()
    {
        _gh.IssueTotal = 1500;
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        _gh.List.SetResult(ListOf(GitHubList.Limit));

        await list.LoadAsync(TestContext.Current.CancellationToken);

        list.TabText.ShouldBe("Issues 1500");
        list.CutShortMessage.ShouldBe("Showing the newest 1000 of 1500. The rest are on GitHub.");
        list.Rows.Count.ShouldBe(1000);
        activity.ForFolder("app")!.OpenIssues.ShouldBe(1500);
    }

    [Fact]
    public async Task A_list_that_just_fits_is_not_cut_short()
    {
        _gh.IssueTotal = 1000;
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        _gh.List.SetResult(ListOf(GitHubList.Limit));

        await list.LoadAsync(TestContext.Current.CancellationToken);

        list.TabText.ShouldBe("Issues 1000");
        list.CutShortMessage.ShouldBeNull();
    }

    [Fact]
    public async Task A_list_of_exactly_the_limit_with_one_odd_row_is_not_cut_short()
    {
        _gh.IssueTotal = 1000;
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        _gh.List.SetResult(ListOf(GitHubList.Limit).Replace("\"number\":2,", "\"number\":\"2\",", StringComparison.Ordinal));

        await list.LoadAsync(TestContext.Current.CancellationToken);

        list.TabText.ShouldBe("Issues 1000");
        list.CutShortMessage.ShouldBeNull();
        list.SkippedMessage.ShouldBe("1 row could not be shown.");
    }

    [Fact]
    public async Task A_list_below_the_limit_sets_the_count_and_says_nothing()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        _gh.List.SetResult(ListOf(999));

        await list.LoadAsync(TestContext.Current.CancellationToken);

        list.TabText.ShouldBe("Issues 999");
        list.CutShortMessage.ShouldBeNull();
        activity.ForFolder("app")!.OpenIssues.ShouldBe(999);
    }

    [Fact]
    public async Task A_skipped_row_still_counts()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        _gh.List.SetResult(ListOf(3).Replace("\"number\":2,", "\"number\":\"2\",", StringComparison.Ordinal));

        await list.LoadAsync(TestContext.Current.CancellationToken);

        list.Rows.Select(r => r.NumberText).ShouldBe(["#1", "#3"]);
        list.TabText.ShouldBe("Issues 3", "gh returned every row, the odd one included: that is the count");
        list.CutShortMessage.ShouldBeNull();
        list.SkippedMessage.ShouldBe("1 row could not be shown.");
        activity.ForFolder("app")!.OpenIssues.ShouldBe(3);
    }

    [Fact]
    public async Task Several_skipped_rows_are_counted_in_one_line_and_none_is_no_line()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        _gh.List.SetResult(ListOf(4).Replace("\"number\":2,", "\"number\":\"2\",", StringComparison.Ordinal).Replace("\"number\":3,", "\"number\":\"3\",", StringComparison.Ordinal));

        await list.LoadAsync(TestContext.Current.CancellationToken);

        list.SkippedMessage.ShouldBe("2 rows could not be shown.");

        var clean = Open(activity, pill).Shown;
        clean.SkippedMessage.ShouldBeNull();
    }

    /// <summary>A panel with that many rows, measured and arranged.</summary>
    private async Task<GitHubPanelViewModel> ListWithRowsAsync(int rows)
    {
        _gh.IssueTotal = rows;
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        _gh.List.SetResult(ListOf(rows));
        await list.LoadAsync(TestContext.Current.CancellationToken);
        return model;
    }

    private static List<Button> RowButtons(DependencyObject panel) => Descendants(panel).OfType<Button>().Where(b => b.DataContext is GitHubRow && b.Focusable).ToList();

    [Fact]
    public async Task A_thousand_rows_build_far_fewer_row_containers()
    {
        var model = await ListWithRowsAsync(1000);
        var list = model.Shown;

        await StaThread.RunAsync(() =>
        {
            var built = RowButtons(PanelView(model)).Count;

            built.ShouldBeGreaterThan(0);
            built.ShouldBeLessThan(100);
        });
    }

    [Fact]
    public async Task Tab_past_the_visible_rows_scrolls_to_the_next_row_and_stays_in_the_panel()
    {
        var model = await ListWithRowsAsync(1000);
        var list = model.Shown;

        await StaThread.RunAsync(() =>
        {
            var (_, panel, source) = FocusedPanel(model);
            using var _ = source;
            var visible = RowButtons(panel).Count;
            visible.ShouldBeInRange(1, 99); // the rows in view only, so that the loop below goes past them

            for (var step = 1; step <= visible + 5; step++)
            {
                ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                panel.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                // the keyboard stays inside the panel and lands on the next row, and the next
                ((Button)Keyboard.FocusedElement).DataContext.ShouldBeSameAs(list.Rows[step]);
            }
        });
    }

    [Fact]
    public async Task Shift_Tab_from_the_close_button_lands_on_a_row_of_the_panel()
    {
        var model = await ListWithRowsAsync(1000);
        var list = model.Shown;

        await StaThread.RunAsync(() =>
        {
            var (_, panel, source) = FocusedPanel(model);
            using var _ = source;
            var close = Descendants(panel).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Close the list");
            Keyboard.Focus(close);

            close.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
            panel.UpdateLayout();

            // With virtualization Shift+Tab cannot reach the last row of the list: the rows past the view are not built. The cycle
            // wraps to the last row that is built, so the keyboard stays in the panel, on a row in view.
            var landed = ((Button)Keyboard.FocusedElement).DataContext.ShouldBeOfType<GitHubRow>();
            var at = list.Rows.ToList().IndexOf(landed);
            at.ShouldBeGreaterThan(0);
            at.ShouldBeLessThan(100);
            RowButtons(panel).ShouldContain(b => ReferenceEquals(b.DataContext, landed));
        });
    }

    [Fact]
    public async Task The_down_arrow_past_the_visible_rows_scrolls_to_the_next_row()
    {
        var model = await ListWithRowsAsync(1000);
        var list = model.Shown;

        await StaThread.RunAsync(() =>
        {
            var (_, panel, source) = FocusedPanel(model);
            using var _ = source;
            var visible = RowButtons(panel).Count;
            visible.ShouldBeInRange(1, 99);

            for (var step = 1; step <= visible + 5; step++)
            {
                ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Down));
                panel.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                ((Button)Keyboard.FocusedElement).DataContext.ShouldBeSameAs(list.Rows[step]);
            }
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child))
            {
                yield return deeper;
            }
        }
    }

    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>The view needs the application's resources to load, so its markup is read from the file.</summary>
    private static XElement YardViewXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "CodeSwitchX.UI", "Yard", "YardView.xaml")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull();
        return XDocument.Load(Path.Combine(dir.FullName, "src", "CodeSwitchX.UI", "Yard", "YardView.xaml")).Root!;
    }

    [Fact]
    public async Task A_row_s_copy_button_puts_its_link_on_the_clipboard_and_opens_nothing()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;

        model.CopyRowLinkCommand.Execute(list.Rows[0]);

        _clipboard.Text.ShouldBe("https://github.com/acme/app/issues/1");
        model.CopyMessage.ShouldBe("Copied the link to #1.");
        _started.ShouldBeEmpty();
    }

    [Fact]
    public async Task Copy_all_puts_every_link_on_the_clipboard_one_to_a_line_in_the_list_s_order()
    {
        var model = await ListWithRowsAsync(3);
        var list = model.Shown;

        model.CopyAllLinksCommand.Execute(null);

        _clipboard.Text.ShouldBe(string.Join(Environment.NewLine,
            "https://github.com/acme/app/issues/1", "https://github.com/acme/app/issues/2", "https://github.com/acme/app/issues/3"));
        model.CopyMessage.ShouldBe("Copied 3 links.");
    }

    [Fact]
    public async Task One_link_copied_is_said_in_the_singular()
    {
        var model = await ListWithRowsAsync(1);
        var list = model.Shown;

        model.CopyAllLinksCommand.Execute(null);

        _clipboard.Text.ShouldBe("https://github.com/acme/app/issues/1");
        model.CopyMessage.ShouldBe("Copied 1 link.");
    }

    [Fact]
    public async Task Copying_all_while_the_list_still_loads_says_it_is_not_all_of_them()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;

        model.CopyAllLinksCommand.Execute(null);

        model.CopyMessage.ShouldBe("Copied 1 link. The full list is still loading.");
    }

    [Fact]
    public async Task Copying_all_of_a_list_cut_short_says_the_rest_are_not_in_it()
    {
        var model = await ListCutShortAsync();
        var list = model.Shown;

        model.CopyAllLinksCommand.Execute(null);

        model.CopyMessage.ShouldBe("Copied the newest 1000 links. The rest are on GitHub.");
    }

    [Fact]
    public async Task Copying_all_after_a_failed_load_says_it_is_not_all_of_them()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        _gh.List.SetResult(null);
        await list.LoadAsync(TestContext.Current.CancellationToken);

        model.CopyAllLinksCommand.Execute(null);

        model.CopyMessage.ShouldBe("Copied 1 link. The full list could not be loaded.");
    }

    [Fact]
    public async Task Copying_all_of_a_list_with_skipped_rows_says_they_are_missing()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        _gh.List.SetResult(ListOf(3).Replace("\"number\":2,", "\"number\":\"2\",", StringComparison.Ordinal));
        await list.LoadAsync(TestContext.Current.CancellationToken);

        model.CopyAllLinksCommand.Execute(null);

        model.CopyMessage.ShouldBe("Copied 2 links. 1 row could not be shown.");
    }

    /// <summary>Runs what is posted only when asked, as a busy UI thread does.</summary>
    private sealed class QueueDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _queue = new();

        public void Post(Action action) => _queue.Enqueue(action);

        public void Post<T>(Action<T> action, T state) => _queue.Enqueue(() => action(state));

        public void Run()
        {
            while (_queue.TryDequeue(out var action))
            {
                action();
            }
        }
    }

    [Fact]
    public async Task A_clear_already_on_its_way_does_not_take_the_line_of_a_newer_copy()
    {
        var (activity, pill) = await PillAsync();
        var ui = new QueueDispatcher();
        var model = new GitHubPanelViewModel(pill.Activity, pill.Kind, new GitHubReader(_gh), activity, Browsers(), _clipboard, ui, _time, NullLogger.Instance, _closedLists.Add);
        var list = model.Shown;
        model.CopyRowLinkCommand.Execute(list.Rows[0]);

        _time.Advance(GitHubPanelViewModel.CopyMessageLifetime); // its clear is posted, the UI thread is in the next copy: a held clipboard takes it up to a second
        _clipboard.Works = false;
        model.CopyRowLinkCommand.Execute(list.Rows[0]);
        ui.Run();

        model.CopyMessage.ShouldBe("Couldn't reach the clipboard. Try again.");
    }

    private async Task<GitHubPanelViewModel> ListCutShortWithASkippedRowAsync()
    {
        _gh.IssueTotal = 1500;
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        _gh.List.SetResult(ListOf(GitHubList.Limit).Replace("\"number\":2,", "\"number\":\"2\",", StringComparison.Ordinal));
        await list.LoadAsync(TestContext.Current.CancellationToken);
        return model;
    }

    [Fact]
    public async Task Copying_all_of_a_list_cut_short_with_a_skipped_row_says_both()
    {
        var model = await ListCutShortWithASkippedRowAsync();
        var list = model.Shown;

        model.CopyAllLinksCommand.Execute(null);

        model.CopyMessage.ShouldBe("Copied the newest 999 links. The rest are on GitHub. 1 row could not be shown.");
    }

    [Fact]
    public async Task On_a_narrow_panel_a_long_copy_line_does_not_move_the_rows_under_the_pointer()
    {
        var model = await ListCutShortWithASkippedRowAsync();
        var list = model.Shown;

        await StaThread.RunAsync(() =>
        {
            var panel = PanelView(model);
            panel.Children.OfType<Border>().Single().Width = 300; // the panel gives way to a narrow Yard
            panel.UpdateLayout();
            double FirstRowTop() => RowButtons(panel)[0].TranslatePoint(new Point(0, 0), panel).Y;
            var before = FirstRowTop();

            model.CopyAllLinksCommand.Execute(null);
            panel.UpdateLayout();

            FirstRowTop().ShouldBe(before);
        });
    }

    [Fact]
    public async Task An_empty_list_has_nothing_to_copy()
    {
        var model = await ListWithRowsAsync(0);
        var list = model.Shown;

        model.CopyAllLinksCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task A_clipboard_another_program_holds_says_so()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        _clipboard.Works = false;

        model.CopyRowLinkCommand.Execute(list.Rows[0]);

        model.CopyMessage.ShouldBe("Couldn't reach the clipboard. Try again.");
    }

    [Fact]
    public async Task The_copy_line_goes_away_after_a_moment_counted_from_the_last_copy()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        model.CopyRowLinkCommand.Execute(list.Rows[0]);

        _time.Advance(GitHubPanelViewModel.CopyMessageLifetime - TimeSpan.FromSeconds(1));
        model.CopyAllLinksCommand.Execute(null);
        _time.Advance(TimeSpan.FromSeconds(1));
        model.CopyMessage.ShouldNotBeNull("the second copy's line has its own time");

        _time.Advance(GitHubPanelViewModel.CopyMessageLifetime);
        model.CopyMessage.ShouldBeNull();
    }

    [Fact]
    public async Task The_panel_has_a_copy_button_on_each_row_and_one_for_all_with_the_row_still_one_tab_stop()
    {
        var model = await ListWithRowsAsync(3);
        var list = model.Shown;

        await StaThread.RunAsync(() =>
        {
            var panel = PanelView(model);
            var buttons = Descendants(panel).OfType<Button>().ToList();

            buttons.Single(b => AutomationProperties.GetName(b) == "Copy all links").Command.ShouldBe(model.CopyAllLinksCommand);
            var copy = buttons.Single(b => AutomationProperties.GetName(b) == "Copy the link to #2");
            copy.Command.ShouldBe(model.CopyRowLinkCommand);
            copy.CommandParameter.ShouldBe(list.Rows[1]);
            copy.Focusable.ShouldBeFalse("the row stays one stop, and a click leaves the keyboard where it was");
            copy.Visibility.ShouldBe(Visibility.Visible);
            copy.ActualWidth.ShouldBeGreaterThan(0);
        });
    }

    [Fact]
    public async Task A_copy_line_does_not_move_the_rows_under_the_pointer()
    {
        var model = await ListWithRowsAsync(3);
        var list = model.Shown;

        await StaThread.RunAsync(() =>
        {
            var panel = PanelView(model);
            double FirstRowTop() => RowButtons(panel)[0].TranslatePoint(new Point(0, 0), panel).Y;
            var before = FirstRowTop();

            model.CopyRowLinkCommand.Execute(list.Rows[0]);
            panel.UpdateLayout();

            FirstRowTop().ShouldBe(before);
        });
    }

    private async Task<GitHubPanelViewModel> ListCutShortAsync()
    {
        _gh.IssueTotal = 1500;
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var list = model.Shown;
        _gh.List.SetResult(ListOf(GitHubList.Limit));
        await list.LoadAsync(TestContext.Current.CancellationToken);
        return model;
    }

    private sealed class FakeClipboard : ITextClipboard
    {
        public bool Works { get; set; } = true;

        public string? Text { get; private set; }

        public bool TrySetText(string text)
        {
            if (Works)
            {
                Text = text;
            }

            return Works;
        }
    }

    private YardViewModel NewYard(GitHubActivity? activity)
    {
        var store = Substitute.For<IWorkspaceStore>();
        var resolver = new WorkspaceResolver();
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var engine = new SessionEngine(bus, resolver, _time, NullLogger<SessionEngine>.Instance);
        var pricing = Substitute.For<IPricingProvider>();
        return new YardViewModel(store, new WorkspaceRegistry(store, resolver, bus), engine, pricing,
            new GitInspector((_, _, _) => Task.FromResult<string?>(string.Empty)), bus, new ImmediateDispatcher(), _time,
            NullLogger<YardViewModel>.Instance, gitHub: activity, gitHubReader: activity is null ? null : new GitHubReader(_gh), gitHubBrowsers: activity is null ? null : Browsers());
    }

    private sealed class FakeSource : IBrowserSource
    {
        public IReadOnlyList<(string Key, string? Executable)> Installed() => [("Firefox-1A2B", @"C:\ff\firefox.exe")];
        public string? FirefoxProfilesIni() => "[Profile0]\nName=alpha\n[Profile1]\nName=beta\n";
        public string? ChromiumLocalState(string folder) => null;
    }
}
