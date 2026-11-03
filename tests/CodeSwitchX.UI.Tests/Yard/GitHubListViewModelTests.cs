using System.Diagnostics;
using CodeSwitchX.Core.GitHub;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting.Browsers;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.GitHub;
using CodeSwitchX.UI.Yard;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Yard;

public class GitHubListViewModelTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeGh _gh = new();
    private readonly List<ProcessStartInfo> _started = [];
    private readonly List<GitHubListViewModel> _closedLists = [];
    private Func<ProcessStartInfo, bool> _start;

    private sealed class FakeGh : IGhCli
    {
        /// <summary>What the pill's read finds: one issue, 12 in all.</summary>
        private const string Graphql = """
            {"data":{"repository":{"viewerPermission":"ADMIN",
             "issues":{"totalCount":12,"nodes":[{"number":1,"title":"First","url":"https://github.com/acme/app/issues/1","updatedAt":"2026-09-23T06:00:00Z","labels":{"nodes":[]}}]},
             "pullRequests":{"totalCount":1,"nodes":[{"number":9,"title":"Ninth","url":"https://github.com/acme/app/pull/9","updatedAt":"2026-09-23T06:00:00Z","headRefName":"b","author":{"login":"me"},"labels":{"nodes":[]}}]}}}}
            """;

        /// <summary>The next issue list read answers with this; null fails it. Awaited, so a test can hold the answer back.</summary>
        public TaskCompletionSource<string?> List { get; } = new();

        /// <summary>The same for the pull request list, which stays unanswered unless a test answers it.</summary>
        public TaskCompletionSource<string?> PullRequestList { get; } = new();

        public Task<IReadOnlyList<string>> AccountsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["me"]);

        public Task<string?> RunAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct) =>
            arguments[0] switch { "api" => Task.FromResult<string?>(Graphql), "pr" => PullRequestList.Task, _ => List.Task };
    }

    public GitHubListViewModelTests()
    {
        _start = info => { _started.Add(info); return true; };
    }

    private static string ListOf(int count) => "[" + string.Join(",", Enumerable.Range(1, count).Select(n =>
        $$"""{"number":{{n}},"title":"Item {{n}}","url":"https://github.com/acme/app/issues/{{n}}","updatedAt":"2026-09-23T06:00:00Z","labels":[]}""")) + "]";

    private GitHubBrowsers Browsers(Func<ProcessStartInfo, bool>? start = null) =>
        new(new BrowserCatalog(new FakeSource()), Substitute.For<ISettingsStore>(), start ?? (info => _start(info)), NullLogger<GitHubBrowsers>.Instance);

    /// <summary>An activity that has read the repository (12 issues, 1 pull request) and the pill of one kind of it.</summary>
    private async Task<(GitHubActivity Activity, GitHubPill Pill)> PillAsync(GitHubItemKind kind = GitHubItemKind.Issue)
    {
        var activity = new GitHubActivity(new GitHubReader(_gh), (_, _) => Task.FromResult<string?>("https://github.com/acme/app.git"),
            NullLogger<GitHubActivity>.Instance);
        await activity.RefreshAsync(["app"], TestContext.Current.CancellationToken);
        return (activity, new GitHubPill(activity.ForFolder("app")!, kind));
    }

    private GitHubListViewModel List(GitHubActivity activity, GitHubPill pill, GitHubBrowsers? browsers = null) =>
        new(pill, new GitHubReader(_gh), activity, browsers ?? Browsers(), new ImmediateDispatcher(), _time, NullLogger.Instance, _closedLists.Add);

    [Fact]
    public async Task Opening_shows_last_known_rows_at_once_then_the_fresh_list()
    {
        var (activity, pill) = await PillAsync();
        var list = List(activity, pill);

        list.Rows.Select(r => r.NumberText).ShouldBe(["#1"]);

        var load = list.LoadAsync(TestContext.Current.CancellationToken);
        list.Rows.Count.ShouldBe(1);
        _gh.List.SetResult(ListOf(3));
        await load;

        list.Rows.Select(r => r.NumberText).ShouldBe(["#1", "#2", "#3"]);
        list.Rows[0].AgeText.ShouldBe("updated today");
        list.Message.ShouldBeNull();
    }

    [Fact]
    public async Task Heading_names_the_repo_and_count()
    {
        var (activity, pill) = await PillAsync();
        _gh.List.SetResult(ListOf(19));

        var issues = List(activity, pill);
        await issues.LoadAsync(TestContext.Current.CancellationToken);

        List(activity, new GitHubPill(pill.Activity, GitHubItemKind.PullRequest)).Heading.ShouldBe("app: 1 open pull request");
        issues.Heading.ShouldBe("app: 19 open issues");
    }

    [Fact]
    public async Task AccountLine_names_account_and_browser()
    {
        var (activity, pill) = await PillAsync();
        var browsers = Browsers();
        browsers.SetChoice("me", "firefox|alpha");

        List(activity, pill, browsers).AccountLine.ShouldBe("Read with me. Opens in Firefox (alpha).");
    }

    [Fact]
    public async Task A_fresh_list_updates_the_tile_count()
    {
        var (activity, pill) = await PillAsync();
        var list = List(activity, pill);
        _gh.List.SetResult(ListOf(4));

        await list.LoadAsync(TestContext.Current.CancellationToken);

        activity.ForFolder("app")!.OpenIssues.ShouldBe(4);
    }

    [Fact]
    public async Task A_failed_load_keeps_the_rows_and_says_so()
    {
        var (activity, pill) = await PillAsync();
        var list = List(activity, pill);
        _gh.List.SetResult(null);

        await list.LoadAsync(TestContext.Current.CancellationToken);

        list.Message.ShouldBe("Couldn't load the latest list. Showing what was last read.");
        list.Rows.Select(r => r.NumberText).ShouldBe(["#1"]);
        activity.ForFolder("app")!.OpenIssues.ShouldBe(12);
    }

    [Fact]
    public async Task Clicking_a_row_opens_it_and_keeps_the_panel_open()
    {
        var (activity, pill) = await PillAsync();
        var list = List(activity, pill);

        list.OpenRowCommand.Execute(list.Rows[0]);

        _started.ShouldHaveSingleItem().FileName.ShouldBe("https://github.com/acme/app/issues/1");
        list.Message.ShouldBeNull();
        _closedLists.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_browser_that_cannot_open_shows_its_line_in_the_panel()
    {
        var (activity, pill) = await PillAsync();
        var browsers = Browsers();
        browsers.SetChoice("me", "firefox|alpha");
        _start = _ => false;
        var list = List(activity, pill, browsers);

        list.OpenRowCommand.Execute(list.Rows[0]);

        list.Message.ShouldBe("Couldn't open Firefox (alpha). Pick another browser in Settings, GitHub.");
        _closedLists.ShouldBeEmpty();
    }

    [Fact]
    public async Task Closing_while_loading_changes_nothing()
    {
        var (activity, pill) = await PillAsync();
        var list = List(activity, pill);
        var load = list.LoadAsync(TestContext.Current.CancellationToken);

        list.CloseCommand.Execute(null);
        _gh.List.SetResult(ListOf(3));
        await load;

        _closedLists.ShouldBe([list]);
        list.Rows.Count.ShouldBe(1);
        list.Heading.ShouldBe("app: 12 open issues");
        activity.ForFolder("app")!.OpenIssues.ShouldBe(3);
    }

    [Fact]
    public async Task A_cancelled_load_changes_nothing_and_throws_nothing()
    {
        var (activity, pill) = await PillAsync();
        var list = List(activity, pill);
        using var stop = new CancellationTokenSource();
        var load = list.LoadAsync(stop.Token);

        stop.Cancel();
        _gh.List.SetCanceled(stop.Token);
        await load;

        list.Rows.Count.ShouldBe(1);
        list.Message.ShouldBeNull();
        activity.ForFolder("app")!.OpenIssues.ShouldBe(12);
    }

    [Fact]
    public async Task A_reader_that_throws_shows_the_failed_load_line()
    {
        var (activity, pill) = await PillAsync();
        var list = List(activity, pill);
        _gh.List.SetException(new InvalidOperationException("boom"));

        await list.LoadAsync(TestContext.Current.CancellationToken);

        list.Message.ShouldBe("Couldn't load the latest list. Showing what was last read.");
        list.Rows.Select(r => r.NumberText).ShouldBe(["#1"]);
    }

    [Fact]
    public async Task Opening_another_pill_replaces_the_list()
    {
        var (activity, pill) = await PillAsync();
        var yard = NewYard(activity);
        yard.OpenGitHubListCommand.Execute(pill);
        var first = yard.GitHubList;
        first.ShouldNotBeNull();

        yard.OpenGitHubListCommand.Execute(new GitHubPill(pill.Activity, GitHubItemKind.PullRequest));

        var second = yard.GitHubList;
        second.ShouldNotBeNull().ShouldNotBeSameAs(first);
        second.Heading.ShouldBe("app: 1 open pull request");
        // The earlier load finishes now and finds its panel gone.
        _gh.List.SetResult(ListOf(3));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        yard.GitHubList.ShouldBeSameAs(second);
        first.Rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Without_the_readers_a_click_does_nothing()
    {
        var (_, pill) = await PillAsync();
        var yard = NewYard(null);

        yard.OpenGitHubListCommand.Execute(pill);

        yard.GitHubList.ShouldBeNull();
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
        public string? EdgeLocalState() => null;
        public string? ChromeLocalState() => null;
    }
}
