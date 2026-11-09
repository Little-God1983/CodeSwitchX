using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using CodeSwitchX.Core.GitHub;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Tests.Infrastructure;
using CodeSwitchX.UI.Yard;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>The panel's Pull button (#407).</summary>
public partial class GitHubListViewModelTests
{
    private readonly FakePullGit _git = new();

    private YardGitJobs NewJobs() => new(new GitPuller(_git.RunAsync), new ImmediateDispatcher(), NullLogger.Instance);

    /// <summary>A panel opened from a line in <c>C:\app</c> on <paramref name="branch"/>, of a repository whose default branch is <paramref name="main"/>.</summary>
    private async Task<GitHubPanelViewModel> OpenOnLine(string? branch = "feature/x", string? main = "main", Func<bool>? working = null, YardGitJobs? jobs = null)
    {
        var (activity, pill) = await PillAsync();
        var repo = pill.Activity with { DefaultBranch = main };
        return new GitHubPanelViewModel(repo, GitHubItemKind.PullRequest, new GitHubReader(_gh), activity, Browsers(), _clipboard, new ImmediateDispatcher(), _time,
            NullLogger.Instance, _closedLists.Add, new PanelCheckout(@"C:\app", branch, working ?? (() => false), jobs ?? NewJobs()));
    }

    [Fact]
    public async Task The_pull_button_names_the_default_branch_and_whether_the_checkout_is_on_it()
    {
        (await OpenOnLine("feature/x")).PullText.ShouldBe("Switch to main and pull");
        (await OpenOnLine("main")).PullText.ShouldBe("Pull main");
        (await OpenOnLine("feature/x", main: "develop")).PullText.ShouldBe("Switch to develop and pull");
        (await OpenOnLine("main")).PullAutomationName.ShouldBe("Pull main in app");
    }

    [Fact]
    public async Task Without_a_default_branch_or_a_checkout_there_is_no_pull()
    {
        var empty = await OpenOnLine(main: null);
        var (activity, pill) = await PillAsync();
        var noCheckout = Open(activity, new GitHubPill(pill.Activity with { DefaultBranch = "main" }, GitHubItemKind.Issue));

        empty.PullText.ShouldBeNull();
        empty.PullCommand.CanExecute(null).ShouldBeFalse();
        noCheckout.PullText.ShouldBeNull();
        noCheckout.PullCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task A_working_chat_refuses_the_pull_and_git_is_not_run()
    {
        var model = await OpenOnLine(working: () => true);

        model.PullCommand.Execute(null);

        model.ActionMessage.ShouldBe("A chat is working here. Pull when it is done.");
        model.IsActionFailed.ShouldBeTrue();
        _git.Ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_pull_turns_the_button_off_while_it_runs_then_says_what_it_did_and_the_branch_moves()
    {
        var model = await OpenOnLine();
        _git.Hold();

        model.PullCommand.Execute(null);

        model.PullCommand.CanExecute(null).ShouldBeFalse();
        model.ActionMessage.ShouldBe("Pulling main...");
        model.IsActionFailed.ShouldBeFalse();
        _git.Release();
        await Eventually(() => model.PullCommand.CanExecute(null));
        model.ActionMessage.ShouldBe("app is on main, 3 new commits.");
        model.IsActionFailed.ShouldBeFalse();
        model.PullText.ShouldBe("Pull main");
    }

    [Fact]
    public async Task A_refused_pull_says_why_and_the_branch_stays()
    {
        _git.Status = " M a.txt\n";
        var model = await OpenOnLine();

        model.PullCommand.Execute(null);

        await Eventually(() => model.ActionMessage == "Pull stopped: 1 file has uncommitted changes.");
        model.IsActionFailed.ShouldBeTrue();
        model.PullText.ShouldBe("Switch to main and pull");
    }

    [Fact]
    public async Task A_panel_closed_while_its_pull_runs_lets_it_finish_and_the_next_panel_of_the_line_shows_how_it_ended()
    {
        var jobs = NewJobs();
        var ended = new List<(GitJobOutcome Outcome, bool Taken)>();
        jobs.Ended += (outcome, taken) => ended.Add((outcome, taken));
        var first = await OpenOnLine(jobs: jobs);
        _git.Hold();
        first.PullCommand.Execute(null);

        first.CloseCommand.Execute(null);
        var second = await OpenOnLine(jobs: jobs);
        second.PullCommand.CanExecute(null).ShouldBeFalse("one pull of a checkout at a time");
        second.ActionMessage.ShouldBe("Pulling main...");
        second.CloseCommand.Execute(null);
        _git.Release();
        await Eventually(() => ended.Count == 1);
        var third = await OpenOnLine(jobs: jobs);

        ended[0].Taken.ShouldBeFalse("no panel was open");
        third.ActionMessage.ShouldBe("app is on main, 3 new commits.");
        first.ActionMessage.ShouldBe("Pulling main...", "a closed panel is let go of");
        (await OpenOnLine(jobs: jobs)).ActionMessage.ShouldBeNull("an outcome is shown once");
    }

    [Fact]
    public async Task An_outcome_a_panel_shows_is_taken()
    {
        var jobs = NewJobs();
        var taken = new List<bool>();
        jobs.Ended += (_, t) => taken.Add(t);
        var model = await OpenOnLine(jobs: jobs);

        model.PullCommand.Execute(null);

        await Eventually(() => taken.Count == 1);
        taken.ShouldBe([true]);
    }

    [Fact]
    public async Task The_view_shows_the_pull_button_above_the_tabs_and_turns_it_off_while_the_pull_runs()
    {
        var model = await OpenOnLine();
        var (activity, pill) = await PillAsync();
        var none = Open(activity, pill);
        _git.Hold();

        await StaThread.RunAsync(() =>
        {
            var view = PanelView(model);
            var button = Descendants<Button>(view).Single(b => AutomationProperties.GetName(b) == "Switch to main and pull in app");
            button.Content.ShouldBe("Switch to main and pull");
            button.IsEnabled.ShouldBeTrue();
            var tabs = Descendants<Button>(view).First(b => AutomationProperties.GetName(b) == "Show pull requests");
            button.TranslatePoint(new Point(0, 0), view).Y.ShouldBeLessThan(tabs.TranslatePoint(new Point(0, 0), view).Y, "the button sits above the tabs");

            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
            view.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            button.IsEnabled.ShouldBeFalse();
            Descendants<TextBlock>(view).ShouldContain(t => t.Text == "Pulling main..." && t.Visibility == Visibility.Visible);

            var without = PanelView(none);
            var hidden = Descendants<Button>(without).Single(b => ReferenceEquals(b.Command, none.PullCommand));
            ((FrameworkElement)VisualTreeHelper.GetParent(hidden)).Visibility.ShouldBe(Visibility.Collapsed, "a panel with no checkout has no pull");
        });
        _git.Release();
    }

    private static List<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var found = new List<T>();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                found.Add(match);
            }

            found.AddRange(Descendants<T>(child));
        }

        return found;
    }
}
