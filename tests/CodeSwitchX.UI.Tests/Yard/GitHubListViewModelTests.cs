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
    private readonly List<ProcessStartInfo> _started = [];
    private readonly List<GitHubListViewModel> _closedLists = [];
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

        public Task<IReadOnlyList<string>> AccountsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["me"]);

        /// <summary>Set when a list read reaches gh, after <see cref="ListThread"/>.</summary>
        public ManualResetEventSlim ListAsked { get; } = new();

        public int ListThread { get; private set; }

        /// <summary>The token the last list read was given.</summary>
        public CancellationToken ListToken { get; private set; }

        public async Task<GhAnswer> AnswerAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct) =>
            new(await RunAsAsync(login, arguments, ct));

        public Task<string?> RunAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct, TimeSpan? timeout = null)
        {
            if (arguments[0] == "api")
            {
                return Task.FromResult<string?>(Graphql);
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
        list.LoadMessage.ShouldBeNull();
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

        list.LoadMessage.ShouldBe("Couldn't load the latest list. Showing what was last read.");
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
        list.OpenMessage.ShouldBeNull();
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

        list.OpenMessage.ShouldBe("Couldn't open Firefox (alpha). Pick another browser in Settings, GitHub.");
        _closedLists.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_row_that_opens_clears_an_earlier_open_failure()
    {
        var (activity, pill) = await PillAsync();
        var browsers = Browsers();
        browsers.SetChoice("me", "firefox|alpha");
        var list = List(activity, pill, browsers);
        _start = _ => false;
        list.OpenRowCommand.Execute(list.Rows[0]);
        list.OpenMessage.ShouldNotBeNull();

        _start = info => { _started.Add(info); return true; };
        list.OpenRowCommand.Execute(list.Rows[0]);

        list.OpenMessage.ShouldBeNull();
    }

    [Fact]
    public async Task A_load_failure_and_an_open_failure_each_keep_their_own_line()
    {
        var (activity, pill) = await PillAsync();
        var browsers = Browsers();
        browsers.SetChoice("me", "firefox|alpha");
        var list = List(activity, pill, browsers);
        _gh.List.SetResult(null);
        _start = _ => false;

        list.OpenRowCommand.Execute(list.Rows[0]);
        await list.LoadAsync(TestContext.Current.CancellationToken);

        list.OpenMessage.ShouldStartWith("Couldn't open");
        list.LoadMessage.ShouldBe("Couldn't load the latest list. Showing what was last read.");
    }

    [Fact]
    public async Task A_load_that_works_clears_the_load_line_and_leaves_the_open_line()
    {
        var (activity, pill) = await PillAsync();
        var browsers = Browsers();
        browsers.SetChoice("me", "firefox|alpha");
        var list = List(activity, pill, browsers);
        _start = _ => false;
        list.OpenRowCommand.Execute(list.Rows[0]);
        _gh.List.SetResult(ListOf(3));

        await list.LoadAsync(TestContext.Current.CancellationToken);

        list.LoadMessage.ShouldBeNull();
        list.OpenMessage.ShouldStartWith("Couldn't open");
    }

    [Fact]
    public async Task Closing_while_loading_cancels_the_read_and_changes_nothing()
    {
        var (activity, pill) = await PillAsync();
        var list = List(activity, pill);
        var load = list.LoadAsync(TestContext.Current.CancellationToken);
        _gh.ListToken.IsCancellationRequested.ShouldBeFalse();

        list.CloseCommand.Execute(null);
        _gh.ListToken.IsCancellationRequested.ShouldBeTrue("the gh run is not left to run out its timeout");
        _gh.List.SetResult(ListOf(3)); // an answer that was already on its way
        await load;

        _closedLists.ShouldBe([list]);
        list.Rows.Count.ShouldBe(1);
        list.Heading.ShouldBe("app: 12 open issues");
        activity.ForFolder("app")!.OpenIssues.ShouldBe(12, "a late result after the close does not exist");
    }

    [Fact]
    public async Task Opening_another_pill_cancels_the_read_of_the_list_it_replaces()
    {
        var (activity, pill) = await PillAsync();
        var yard = NewYard(activity);
        yard.OpenGitHubListCommand.Execute(pill);
        var first = yard.GitHubList.ShouldNotBeNull();
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
            var list = new GitHubListViewModel(new GitHubPill(activity.ForFolder("app")!, GitHubItemKind.Issue), new GitHubReader(gh), activity, Browsers(),
                new ImmediateDispatcher(), _time, NullLogger.Instance, _closedLists.Add);
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
        var list = List(activity, pill);
        using var stop = new CancellationTokenSource();
        var load = list.LoadAsync(stop.Token);

        stop.Cancel();
        _gh.List.SetCanceled(stop.Token);
        await load;

        list.LoadingMessage.ShouldBeNull();
    }

    [Fact]
    public async Task A_list_that_a_newer_result_overtook_shows_its_rows_with_the_stored_total()
    {
        var (activity, pill) = await PillAsync();
        var list = List(activity, pill);
        var load = list.LoadAsync(TestContext.Current.CancellationToken);
        _gh.IssueTotal = 20;
        await activity.RefreshAsync(["app"], TestContext.Current.CancellationToken); // a round stores a newer result while the list is read
        _gh.List.SetResult(ListOf(3));
        await load;

        list.Rows.Select(r => r.NumberText).ShouldBe(["#1", "#2", "#3"]);
        list.Heading.ShouldBe("app: 20 open issues");
        activity.ForFolder("app")!.OpenIssues.ShouldBe(20);
        activity.ForFolder("app")!.NewestIssues.Count.ShouldBe(1);
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
        list.LoadMessage.ShouldBeNull();
        activity.ForFolder("app")!.OpenIssues.ShouldBe(12);
    }

    [Fact]
    public async Task A_reader_that_throws_shows_the_failed_load_line()
    {
        var (activity, pill) = await PillAsync();
        var list = List(activity, pill);
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
        var first = yard.GitHubList;
        first.ShouldNotBeNull();

        yard.OpenGitHubListCommand.Execute(new GitHubPill(pill.Activity, GitHubItemKind.PullRequest));

        var second = yard.GitHubList;
        second.ShouldNotBeNull().ShouldNotBeSameAs(first);
        second.Heading.ShouldBe("app: 1 open pull request");
        // The earlier load finishes now and finds its panel gone.
        _gh.List.SetResult(ListOf(3));
        await first.Loaded;
        yard.GitHubList.ShouldBeSameAs(second);
        first.Rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Disposing_the_yard_closes_an_open_list()
    {
        var (activity, pill) = await PillAsync();
        var yard = NewYard(activity);
        yard.OpenGitHubListCommand.Execute(pill);
        var list = yard.GitHubList.ShouldNotBeNull();

        yard.Dispose();
        _gh.List.SetResult(ListOf(3));
        await list.Loaded;

        yard.GitHubList.ShouldBeNull();
        list.Rows.Count.ShouldBe(1, "a load that finishes later leaves the closed list alone");
    }

    [Fact]
    public async Task Without_the_readers_a_click_does_nothing()
    {
        var (_, pill) = await PillAsync();
        var yard = NewYard(null);

        yard.OpenGitHubListCommand.Execute(pill);

        yard.GitHubList.ShouldBeNull();
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
            yard.GitHubList.ShouldNotBeNull();

            Escape().ShouldBeTrue();
            yard.GitHubList.ShouldBeNull();
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

    /// <summary>
    /// The list panel as the Yard shows it: its markup from the file, on the application's resources, with the list as the context
    /// of the Yard.
    /// </summary>
    private static Grid Panel(GitHubListViewModel? list)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var root = YardViewXaml();
        var markup = new XElement(root.Descendants(Wpf + "Border").Single(b => (string?)b.Attribute(x + "Name") == "GitHubPanel"));
        // in a Grid of its own: the panel's attributes look their resources up outside it
        var grid = new XElement(Wpf + "Grid", root.Attributes().Where(a => a.IsNamespaceDeclaration),
            new XElement(Wpf + "Grid.Resources", GitPillsXamlTests.AppResourcesXaml()), markup);
        var panel = (Grid)XamlReader.Parse(GitPillsXamlTests.AssemblyQualified(grid.ToString()));
        panel.Children.OfType<Border>().Single().DataContext = new { GitHubList = list };
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
        var list = List(activity, pill);
        _gh.List.SetResult(ListOf(3));
        await list.LoadAsync(TestContext.Current.CancellationToken);

        await StaThread.RunAsync(() =>
        {
            var stops = TabStops(Panel(list).Children.OfType<Border>().Single());

            // the close button, then one button a row
            stops.Select(s => s.GetType().Name).ShouldBe(["Button", "Button", "Button", "Button"]);
        });
    }

    [Fact]
    public async Task The_panel_shows_each_failure_line_only_while_it_has_something_to_say()
    {
        var (activity, pill) = await PillAsync();
        var browsers = Browsers();
        browsers.SetChoice("me", "firefox|alpha");
        var list = List(activity, pill, browsers);
        _start = _ => false;

        await StaThread.RunAsync(() =>
        {
            var panel = Panel(list);
            // the failure lines are the amber ones
            List<TextBlock> Lines() => Descendants(panel).OfType<TextBlock>().Where(t => t.Visibility == Visibility.Visible && t.Foreground is SolidColorBrush b && b.Color == Color.FromRgb(0xF0, 0xC6, 0x74)).ToList();

            Lines().ShouldBe([], "nothing to say, nothing shown");

            list.OpenRowCommand.Execute(list.Rows[0]);
            panel.UpdateLayout();

            var shown = Lines();
            shown.ShouldHaveSingleItem().Text.ShouldStartWith("Couldn't open");
        });
    }

    /// <summary>The panel as the Yard has it, with its focus handling, under a window of its own, and the keyboard on the first row.</summary>
    private static (Grid Grid, Border Panel, HwndSource Source) FocusedPanel(GitHubListViewModel list)
    {
        var grid = Panel(list);
        var source = new HwndSource(new HwndSourceParameters("test") { WindowStyle = 0 }) { RootVisual = grid };
        var panel = grid.Children.OfType<Border>().Single();
        _ = new GitHubPanelFocus(grid, panel, () => { });
        Keyboard.Focus(Descendants(panel).OfType<Button>().First(b => b.DataContext is GitHubRow));
        return (grid, panel, source);
    }

    [Fact]
    public async Task A_swap_of_the_rows_keeps_the_keyboard_in_the_panel()
    {
        var (activity, pill) = await PillAsync();
        var list = List(activity, pill);
        _gh.List.SetResult(ListOf(3));
        await list.LoadAsync(TestContext.Current.CancellationToken);

        await StaThread.RunAsync(() =>
        {
            var (_, panel, source) = FocusedPanel(list);
            using var _ = source;
            panel.IsKeyboardFocusWithin.ShouldBeTrue();

            list.Rows = list.Rows.Take(2).ToList(); // the focused button is rebuilt
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            panel.IsKeyboardFocusWithin.ShouldBeTrue();
            Keyboard.FocusedElement.ShouldBeSameAs(panel);
        });
    }

    [Fact]
    public async Task A_keyboard_that_is_nowhere_without_a_swap_is_not_pulled_back()
    {
        var (activity, pill) = await PillAsync();
        var list = List(activity, pill);
        _gh.List.SetResult(ListOf(3));
        await list.LoadAsync(TestContext.Current.CancellationToken);

        await StaThread.RunAsync(() =>
        {
            var (_, _, source) = FocusedPanel(list);
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
        var list = List(activity, pill);
        _gh.List.SetResult(ListOf(GitHubList.Limit));

        await list.LoadAsync(TestContext.Current.CancellationToken);

        list.Heading.ShouldBe("app: 1500 open issues");
        list.CutShortMessage.ShouldBe("Showing the newest 1000 of 1500. The rest are on GitHub.");
        list.Rows.Count.ShouldBe(1000);
        activity.ForFolder("app")!.OpenIssues.ShouldBe(1500);
    }

    [Fact]
    public async Task A_list_that_just_fits_is_not_cut_short()
    {
        _gh.IssueTotal = 1000;
        var (activity, pill) = await PillAsync();
        var list = List(activity, pill);
        _gh.List.SetResult(ListOf(GitHubList.Limit));

        await list.LoadAsync(TestContext.Current.CancellationToken);

        list.Heading.ShouldBe("app: 1000 open issues");
        list.CutShortMessage.ShouldBeNull();
    }

    [Fact]
    public async Task A_list_below_the_limit_sets_the_count_and_says_nothing()
    {
        var (activity, pill) = await PillAsync();
        var list = List(activity, pill);
        _gh.List.SetResult(ListOf(999));

        await list.LoadAsync(TestContext.Current.CancellationToken);

        list.Heading.ShouldBe("app: 999 open issues");
        list.CutShortMessage.ShouldBeNull();
        activity.ForFolder("app")!.OpenIssues.ShouldBe(999);
    }

    [Fact]
    public async Task A_skipped_row_does_not_lower_the_count()
    {
        var (activity, pill) = await PillAsync();
        var list = List(activity, pill);
        _gh.List.SetResult(ListOf(3).Replace("\"number\":2,", "\"number\":\"2\",", StringComparison.Ordinal));

        await list.LoadAsync(TestContext.Current.CancellationToken);

        list.Rows.Select(r => r.NumberText).ShouldBe(["#1", "#3"]);
        list.Heading.ShouldBe("app: 12 open issues");
        list.CutShortMessage.ShouldBeNull();
        list.SkippedMessage.ShouldBe("1 row could not be shown.");
        activity.ForFolder("app")!.OpenIssues.ShouldBe(12);
    }

    [Fact]
    public async Task Several_skipped_rows_are_counted_in_one_line_and_none_is_no_line()
    {
        var (activity, pill) = await PillAsync();
        var list = List(activity, pill);
        _gh.List.SetResult(ListOf(4).Replace("\"number\":2,", "\"number\":\"2\",", StringComparison.Ordinal).Replace("\"number\":3,", "\"number\":\"3\",", StringComparison.Ordinal));

        await list.LoadAsync(TestContext.Current.CancellationToken);

        list.SkippedMessage.ShouldBe("2 rows could not be shown.");

        var clean = List(activity, pill);
        clean.SkippedMessage.ShouldBeNull();
    }

    /// <summary>A panel with that many rows, measured and arranged.</summary>
    private async Task<GitHubListViewModel> ListWithRowsAsync(int rows)
    {
        _gh.IssueTotal = rows;
        var (activity, pill) = await PillAsync();
        var list = List(activity, pill);
        _gh.List.SetResult(ListOf(rows));
        await list.LoadAsync(TestContext.Current.CancellationToken);
        return list;
    }

    private static List<Button> RowButtons(DependencyObject panel) => Descendants(panel).OfType<Button>().Where(b => b.DataContext is GitHubRow).ToList();

    [Fact]
    public async Task A_thousand_rows_build_far_fewer_row_containers()
    {
        var list = await ListWithRowsAsync(1000);

        await StaThread.RunAsync(() =>
        {
            var built = RowButtons(Panel(list)).Count;

            built.ShouldBeGreaterThan(0);
            built.ShouldBeLessThan(100);
        });
    }

    [Fact]
    public async Task Tab_past_the_visible_rows_scrolls_to_the_next_row_and_stays_in_the_panel()
    {
        var list = await ListWithRowsAsync(1000);

        await StaThread.RunAsync(() =>
        {
            var (_, panel, source) = FocusedPanel(list);
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
        var list = await ListWithRowsAsync(1000);

        await StaThread.RunAsync(() =>
        {
            var (_, panel, source) = FocusedPanel(list);
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
        var list = await ListWithRowsAsync(1000);

        await StaThread.RunAsync(() =>
        {
            var (_, panel, source) = FocusedPanel(list);
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
