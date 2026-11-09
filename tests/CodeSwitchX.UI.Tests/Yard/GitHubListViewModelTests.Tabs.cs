using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CodeSwitchX.Core.GitHub;
using CodeSwitchX.UI.Tests.Infrastructure;
using CodeSwitchX.UI.Yard;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>The panel's two tabs (#410): one template, names with count and state, a count that does not push the other tab.</summary>
public partial class GitHubListViewModelTests
{
    [Fact]
    public async Task Each_tab_is_named_with_its_count_and_whether_it_is_in_front()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, new GitHubPill(pill.Activity, GitHubItemKind.PullRequest));

        model.PullRequests.TabAutomationName.ShouldBe("Pull requests, 1 open, shown");
        model.Issues.TabAutomationName.ShouldBe("Issues, 12 open, not shown");

        model.ShowCommand.Execute(GitHubItemKind.Issue);

        model.PullRequests.TabAutomationName.ShouldBe("Pull requests, 1 open, not shown");
        model.Issues.TabAutomationName.ShouldBe("Issues, 12 open, shown");
        model.Tabs.ShouldBe([model.PullRequests, model.Issues]);
    }

    [Fact]
    public async Task A_new_count_renames_the_tab()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        var changed = new List<string?>();
        model.Issues.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _gh.List.SetResult(ListOf(19));

        await model.Issues.LoadAsync(TestContext.Current.CancellationToken);

        model.Issues.TabAutomationName.ShouldBe("Issues, 19 open, shown");
        changed.ShouldContain(nameof(GitHubListViewModel.TabAutomationName));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1000, "999+")]
    [InlineData(1500, "999+")]
    public async Task The_tab_shows_at_most_999_plus_and_the_screen_reader_hears_the_whole_count(int count, string shown)
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);

        model.Issues.Count = count;

        model.Issues.CountText.ShouldBe(shown);
        model.Issues.TabAutomationName.ShouldBe($"Issues, {count} open, shown");
    }

    [Fact]
    public async Task The_issues_tab_stays_where_it_is_as_the_pull_request_count_grows_and_the_tab_shows_999_plus()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);
        model.PullRequests.Count = 9;

        await StaThread.RunAsync(() =>
        {
            var view = PanelView(model);
            double IssuesTabX() => Descendants<Button>(view).Single(b => AutomationProperties.GetName(b).StartsWith("Issues,", StringComparison.Ordinal))
                .TranslatePoint(new Point(0, 0), view).X;
            var before = IssuesTabX();

            foreach (var count in new[] { 12, 99, 999, 1500 })
            {
                model.PullRequests.Count = count;
                view.UpdateLayout();

                IssuesTabX().ShouldBe(before, $"at {count}");
            }

            // what the tab shows, not only where the next one sits: the count is the visible one of the two in the pull requests tab
            var tab = Descendants<Button>(view).Single(b => AutomationProperties.GetName(b).StartsWith("Pull requests,", StringComparison.Ordinal));
            Descendants<TextBlock>(tab).Where(t => t.Visibility == Visibility.Visible).Select(t => t.Text).ShouldBe(["Pull requests", "999+"]);
        });
    }

    [Fact]
    public async Task The_tab_in_front_has_its_pill_colours_and_the_other_none()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, new GitHubPill(pill.Activity, GitHubItemKind.PullRequest));

        await StaThread.RunAsync(() =>
        {
            var view = PanelView(model);
            Border TabOf(string name) => (Border)Ancestors(Descendants<Button>(view).Single(b => AutomationProperties.GetName(b).StartsWith(name, StringComparison.Ordinal)))
                .First(e => e is Border { CornerRadius.TopLeft: 6 });

            TabOf("Pull requests,").Background.ShouldBeSameAs(view.FindResource("PullRequestPillBack"));
            ((System.Windows.Media.SolidColorBrush)TabOf("Issues,").Background).Color.A.ShouldBe((byte)0);

            model.ShowCommand.Execute(GitHubItemKind.Issue);
            view.UpdateLayout();

            TabOf("Issues,").Background.ShouldBeSameAs(view.FindResource("IssuePillBack"));
        });
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for (var at = System.Windows.Media.VisualTreeHelper.GetParent(element); at is not null; at = System.Windows.Media.VisualTreeHelper.GetParent(at))
        {
            yield return at;
        }
    }
}
