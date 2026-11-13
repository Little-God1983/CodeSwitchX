using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CodeSwitchX.Core.GitHub;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Tests.Infrastructure;
using CodeSwitchX.UI.Yard;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>The panel's Merge button and strip (#408).</summary>
public partial class GitHubListViewModelTests
{
    private readonly FakeMergeGh _merge = new();

    private YardGitJobs MergingJobs() => new(new GitPuller(_git.RunAsync), new ImmediateDispatcher(), _time, NullLogger.Instance, new GitHubMerger(_merge));

    /// <summary>
    /// A panel opened from a line in <c>C:\app</c> on <paramref name="branch"/>, whose one open pull request, #9, comes from branch <c>b</c>
    /// and merges into <paramref name="into"/>; the repository's default branch is main.
    /// </summary>
    private async Task<GitHubPanelViewModel> OpenForMerge(string? branch = "b", string into = "main", bool fromFork = false, Func<bool>? working = null, YardGitJobs? jobs = null,
        string? head = null)
    {
        var (activity, pill) = await PillAsync();
        var pr = pill.Activity.NewestPullRequests.Single() with { BaseBranch = into, IsCrossRepository = fromFork, HeadSha = head };
        var repo = pill.Activity with { DefaultBranch = "main", NewestPullRequests = [pr] };
        return new GitHubPanelViewModel(repo, GitHubItemKind.PullRequest, new GitHubReader(_gh), activity, Browsers(), _clipboard, new ImmediateDispatcher(), _time,
            NullLogger.Instance, _closedLists.Add, new PanelCheckout(@"C:\app", branch, working ?? (() => false), jobs ?? MergingJobs()));
    }

    private static GitHubRow Row(GitHubPanelViewModel model) => model.PullRequests.Rows.Single();

    /// <summary>gh's list of one open pull request, #9 from branch b, at head <paramref name="head"/>.</summary>
    private static string PullRequestNine(string head) =>
        PullRequestsOf(1).Replace("\"number\":1", "\"number\":9", StringComparison.Ordinal)
            .Replace("\"headRefName\":\"b1\"", $"\"headRefName\":\"b\",\"headRefOid\":\"{head}\"", StringComparison.Ordinal);

    [Fact]
    public async Task Merge_only_opens_the_strip_and_merges_nothing()
    {
        var model = await OpenForMerge();

        model.OpenMergeCommand.Execute(Row(model));

        model.MergeStrip.ShouldNotBeNull().Row.ShouldBe(Row(model));
        _merge.Runs.ShouldBeEmpty();
        _git.Ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task On_the_pull_requests_branch_the_strip_offers_merge_and_pull_first()
    {
        var model = await OpenForMerge("b");

        model.OpenMergeCommand.Execute(Row(model));

        model.MergeStrip.ShouldBe(new MergeStrip(Row(model), "Merge #9 into main?", "This workspace is on its branch. Merge and pull switches it to main and pulls.", true, "Merge only", "main"));
    }

    [Theory]
    [InlineData("other", "main", false, "This workspace stays on other.")]
    [InlineData("b", "main", true, "It comes from a fork. This workspace stays on b.")]
    [InlineData("b", "develop", false, "It merges into develop, not main. This workspace stays on b.")]
    public async Task Elsewhere_a_forks_branch_or_another_base_offer_merge_alone(string branch, string into, bool fromFork, string line)
    {
        var model = await OpenForMerge(branch, into, fromFork);

        model.OpenMergeCommand.Execute(Row(model));

        var strip = model.MergeStrip.ShouldNotBeNull();
        strip.OffersPull.ShouldBeFalse();
        strip.MergeText.ShouldBe(into == "main" && !fromFork && branch == "b" ? "Merge only" : "Merge");
        strip.CheckoutLine.ShouldBe(line);
        strip.Question.ShouldBe($"Merge #9 into {into}?");
    }

    [Fact]
    public async Task A_working_chat_leaves_the_switch_out()
    {
        var model = await OpenForMerge("b", working: () => true);

        model.OpenMergeCommand.Execute(Row(model));

        var strip = model.MergeStrip.ShouldNotBeNull();
        strip.OffersPull.ShouldBeFalse();
        strip.MergeText.ShouldBe("Merge only");
        strip.CheckoutLine.ShouldBe("A chat is working here, so the switch is left out.");
    }

    [Fact]
    public async Task Merge_and_pull_merges_as_the_reading_account_then_pulls_and_the_row_leaves()
    {
        var model = await OpenForMerge("b");
        model.OpenMergeCommand.Execute(Row(model));

        model.MergeAndPullCommand.Execute(null);

        model.MergeStrip.ShouldBeNull();
        await Eventually(() => !model.IsBusy && model.ActionMessage is not null);
        model.ActionMessage.ShouldBe("Merged #9. app is on main, 3 new commits.");
        model.IsActionFailed.ShouldBeFalse();
        _merge.Runs.ShouldHaveSingleItem().Login.ShouldBe("me");
        _merge.Runs[0].Arguments.ShouldBe(["pr", "merge", "9", "-R", "acme/app", "--merge"]);
        model.PullRequests.Rows.ShouldBeEmpty();
        model.PullRequests.Count.ShouldBe(0);
        model.PullText.ShouldBe("Pull main");
    }

    [Fact]
    public async Task Merge_only_leaves_the_checkout_alone()
    {
        var model = await OpenForMerge("b");
        model.OpenMergeCommand.Execute(Row(model));

        model.MergeOnlyCommand.Execute(null);

        await Eventually(() => !model.IsBusy && model.ActionMessage is not null);
        model.ActionMessage.ShouldBe("Merged #9.");
        _git.Ran.ShouldBeEmpty();
        model.PullText.ShouldBe("Switch to main and pull");
        model.PullRequests.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_merge_GitHub_refuses_says_why_and_the_row_stays()
    {
        _merge.Answer = new GhAnswer(null, ErrorOutput: "X Pull request acme/app#9 is not mergeable: the merge commit cannot be cleanly created.\n");
        var model = await OpenForMerge("b");
        model.OpenMergeCommand.Execute(Row(model));

        model.MergeAndPullCommand.Execute(null);

        await Eventually(() => !model.IsBusy && model.ActionMessage is not null);
        model.ActionMessage.ShouldBe("Can't merge #9: it has conflicts with main.");
        model.IsActionFailed.ShouldBeTrue();
        _git.Ran.ShouldBeEmpty("nothing is pulled after a merge that did not happen");
        model.PullRequests.Rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_merge_whose_pull_is_refused_says_it_merged_but_did_not_switch()
    {
        _git.Status = " M a.txt\n";
        var model = await OpenForMerge("b");
        model.OpenMergeCommand.Execute(Row(model));

        model.MergeAndPullCommand.Execute(null);

        await Eventually(() => !model.IsBusy && model.ActionMessage is not null);
        model.ActionMessage.ShouldBe("Merged #9, but didn't switch: 1 file has uncommitted changes.");
        model.IsActionFailed.ShouldBeTrue();
        model.PullRequests.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task On_the_branch_it_merges_into_the_strip_offers_merge_and_pull_too()
    {
        var model = await OpenForMerge("main");

        model.OpenMergeCommand.Execute(Row(model));

        model.MergeStrip.ShouldBe(new MergeStrip(Row(model), "Merge #9 into main?", "This workspace is on main. Merge and pull brings it up to date.", true, "Merge only", "main"));
    }

    [Fact]
    public async Task A_fork_merging_into_the_workspaces_branch_offers_merge_and_pull_too()
    {
        var model = await OpenForMerge("main", fromFork: true);

        model.OpenMergeCommand.Execute(Row(model));

        var strip = model.MergeStrip.ShouldNotBeNull();
        strip.OffersPull.ShouldBeTrue("the merge lands on the branch the workspace is on, wherever it comes from");
        strip.CheckoutLine.ShouldBe("This workspace is on main. Merge and pull brings it up to date.");
    }

    [Fact]
    public async Task On_a_base_that_is_not_the_default_branch_the_strip_offers_merge_alone()
    {
        var model = await OpenForMerge("develop", into: "develop");

        model.OpenMergeCommand.Execute(Row(model));

        var strip = model.MergeStrip.ShouldNotBeNull();
        strip.OffersPull.ShouldBeFalse("the Yard pulls the default branch only");
        strip.MergeText.ShouldBe("Merge");
        strip.CheckoutLine.ShouldBe("This workspace stays on develop.");
    }

    [Fact]
    public async Task A_working_chat_on_the_branch_it_merges_into_leaves_the_pull_out()
    {
        var model = await OpenForMerge("main", working: () => true);

        model.OpenMergeCommand.Execute(Row(model));

        var strip = model.MergeStrip.ShouldNotBeNull();
        strip.OffersPull.ShouldBeFalse();
        strip.MergeText.ShouldBe("Merge only");
        strip.CheckoutLine.ShouldBe("A chat is working here, so the pull is left out.");
    }

    [Fact]
    public async Task Merge_and_pull_on_the_branch_it_merges_into_pulls_without_a_switch()
    {
        _git.Current = "main";
        var model = await OpenForMerge("main");
        model.OpenMergeCommand.Execute(Row(model));

        model.MergeAndPullCommand.Execute(null);

        await Eventually(() => !model.IsBusy && model.ActionMessage is not null);
        model.ActionMessage.ShouldBe("Merged #9. app is on main, 3 new commits.");
        model.IsActionFailed.ShouldBeFalse();
        _git.Ran.ShouldContain("merge --ff-only --quiet refs/remotes/origin/main");
        _git.Ran.ShouldNotContain(r => r.StartsWith("switch", StringComparison.Ordinal));
        model.PullText.ShouldBe("Pull main");
    }

    [Fact]
    public async Task A_merge_whose_pull_is_refused_on_the_branch_it_merges_into_says_it_did_not_pull()
    {
        _git.Current = "main";
        _git.Status = " M a.txt\n";
        var model = await OpenForMerge("main");
        model.OpenMergeCommand.Execute(Row(model));

        model.MergeAndPullCommand.Execute(null);

        await Eventually(() => !model.IsBusy && model.ActionMessage is not null);
        model.ActionMessage.ShouldBe("Merged #9, but didn't pull: 1 file has uncommitted changes.");
        model.IsActionFailed.ShouldBeTrue();
    }

    [Fact]
    public async Task One_merged_already_leaves_the_list_and_the_pull_follows()
    {
        _merge.Answer = new GhAnswer(null, ErrorOutput: "X Pull request acme/app#9 was already merged\n");
        var model = await OpenForMerge("b");
        model.OpenMergeCommand.Execute(Row(model));

        model.MergeAndPullCommand.Execute(null);

        await Eventually(() => !model.IsBusy && model.ActionMessage is not null);
        model.ActionMessage.ShouldBe("#9 was merged already. app is on main, 3 new commits.");
        model.PullRequests.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_full_list_read_before_the_merge_does_not_bring_the_merged_row_back()
    {
        var model = await OpenForMerge("b");
        var load = model.PullRequests.LoadAsync(TestContext.Current.CancellationToken);
        model.OpenMergeCommand.Execute(Row(model));
        model.MergeOnlyCommand.Execute(null);
        await Eventually(() => !model.IsBusy && model.ActionMessage is not null);

        _gh.PullRequestList.SetResult(PullRequestsOf(3).Replace("\"number\":3", "\"number\":9", StringComparison.Ordinal));
        await load;

        model.PullRequests.Rows.Select(r => r.NumberText).ShouldBe(["#1", "#2"]);
        model.PullRequests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_branch_change_closes_an_open_strip()
    {
        var model = await OpenForMerge("b");
        model.OpenMergeCommand.Execute(Row(model));

        model.FollowBranch("b");
        model.MergeStrip.ShouldNotBeNull("the same branch keeps it");
        model.FollowBranch("feature/y");

        model.MergeStrip.ShouldBeNull("its choices were worked out from the branch the checkout left");
    }

    [Fact]
    public async Task A_merge_that_ran_out_of_time_still_ends_as_a_merge_job()
    {
        _merge.Answer = new GhAnswer(null, TimedOut: true);
        var jobs = MergingJobs();
        var ended = new List<GitJobOutcome>();
        jobs.Ended += (outcome, _) => ended.Add(outcome);

        await jobs.MergeAsync(@"C:\app", "app", new GitHubRepo("acme", "app"), "me", 9, "main", null, () => false);

        ended.ShouldHaveSingleItem().ChangedGitHub.ShouldBeTrue("the Yard reads GitHub again: it may have merged");
        ended[0].Merged.ShouldBeNull();
    }

    [Fact]
    public async Task A_refused_merge_changed_nothing_on_GitHub()
    {
        _merge.Answer = new GhAnswer(null, ErrorOutput: "X Pull request acme/app#9 is not mergeable: the merge commit cannot be cleanly created.\n");
        var jobs = MergingJobs();
        var ended = new List<GitJobOutcome>();
        jobs.Ended += (outcome, _) => ended.Add(outcome);

        await jobs.MergeAsync(@"C:\app", "app", new GitHubRepo("acme", "app"), "me", 9, "main", null, () => false);

        ended.ShouldHaveSingleItem().ChangedGitHub.ShouldBeFalse("no read of GitHub for nothing");
    }

    [Fact]
    public async Task Merge_and_pull_does_not_leave_a_branch_with_commits_GitHub_has_not_got()
    {
        _git.Unpushed = 2;
        _git.Current = "b";
        var model = await OpenForMerge("b");
        model.OpenMergeCommand.Execute(Row(model));

        model.MergeAndPullCommand.Execute(null);

        await Eventually(() => !model.IsBusy && model.ActionMessage is not null);
        model.ActionMessage.ShouldBe("Merged #9, but didn't switch: b has local commits that aren't on GitHub, so they are not in the merge.");
        _git.Ran.ShouldNotContain(r => r.StartsWith("switch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_merged_pull_request_beyond_the_rows_shown_still_lowers_the_count_once()
    {
        var model = await OpenForMerge("b");
        var before = model.PullRequests.Count;

        model.PullRequests.Drop(400);
        model.PullRequests.Drop(400);

        model.PullRequests.Count.ShouldBe(before - 1);
        model.PullRequests.Rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_merge_goes_for_the_head_the_row_was_read_at()
    {
        var model = await OpenForMerge("b", head: "abc123");
        model.OpenMergeCommand.Execute(Row(model));

        model.MergeOnlyCommand.Execute(null);

        await Eventually(() => !model.IsBusy && model.ActionMessage is not null);
        _merge.Runs.ShouldHaveSingleItem().Arguments.ShouldBe(["pr", "merge", "9", "-R", "acme/app", "--merge", "--match-head-commit", "abc123"]);
    }

    [Fact]
    public async Task A_head_read_after_the_strip_opened_refuses_the_merge_here_and_merges_nothing()
    {
        var model = await OpenForMerge("b", head: "looked-at");
        model.OpenMergeCommand.Execute(Row(model));
        _gh.PullRequestList.SetResult(PullRequestNine("pushed-since"));
        await model.PullRequests.LoadAsync(TestContext.Current.CancellationToken);

        model.MergeOnlyCommand.Execute(null);

        model.ActionMessage.ShouldBe("Can't merge #9: it changed since you opened Merge. Look at it, then merge again.");
        model.IsActionFailed.ShouldBeTrue();
        model.MergeStrip.ShouldBeNull();
        _merge.Runs.ShouldBeEmpty("a commit nobody looked at is not merged");
        model.OpenMergeCommand.Execute(Row(model));
        model.MergeOnlyCommand.Execute(null);
        await Eventually(() => !model.IsBusy && _merge.Runs.Count == 1);
        _merge.Runs[0].Arguments.ShouldContain("pushed-since", "a strip opened again goes for the head it shows now");
    }

    [Fact]
    public async Task A_merge_GitHub_only_queued_keeps_the_row_and_pulls_nothing()
    {
        _merge.Answer = new GhAnswer(string.Empty, ErrorOutput: "! The merge strategy for main is set by the merge queue\n");
        var model = await OpenForMerge("b");
        model.OpenMergeCommand.Execute(Row(model));

        model.MergeAndPullCommand.Execute(null);

        await Eventually(() => !model.IsBusy && model.ActionMessage is not null);
        model.ActionMessage.ShouldBe("#9 will be added to the merge queue. Pull when GitHub has merged it.");
        model.IsActionFailed.ShouldBeFalse();
        model.PullRequests.Rows.Count.ShouldBe(1, "it is not merged yet");
        _git.Ran.ShouldBeEmpty("main does not have it yet");
    }

    [Fact]
    public async Task A_pull_request_that_moved_is_refused_and_the_list_is_read_again()
    {
        _merge.Answer = new GhAnswer(null, ErrorOutput: "GraphQL: Head branch was modified. Review and try the merge again. (mergePullRequest)\n");
        var model = await OpenForMerge("b", head: "old");
        _ = model.PullRequests.LoadAsync(TestContext.Current.CancellationToken); // the tab in front is read when the panel opens; its answer is held
        await Eventually(() => _gh.Asked.Count(a => a == "pr") == 1);
        model.OpenMergeCommand.Execute(Row(model));

        model.MergeOnlyCommand.Execute(null);

        await Eventually(() => !model.IsBusy && model.ActionMessage is not null);
        model.ActionMessage.ShouldBe("Can't merge #9: it changed since the panel read it. Look at it, then merge again.");
        model.IsActionFailed.ShouldBeTrue();
        await Eventually(() => _gh.Asked.Count(a => a == "pr") == 2);
        model.PullRequests.IsLoading.ShouldBeTrue("the list is read again; the next Merge goes for the new head");
        _gh.PullRequestList.SetResult(PullRequestNine("new"));
        await Eventually(() => !model.PullRequests.IsLoading);
        Row(model).Item.HeadSha.ShouldBe("new");
    }

    [Fact]
    public async Task A_moved_pull_request_refreshes_the_tiles_preview_too()
    {
        _merge.Answer = new GhAnswer(null, ErrorOutput: "GraphQL: Head branch was modified. Review and try the merge again. (mergePullRequest)\n");
        var jobs = MergingJobs();
        var ended = new List<GitJobOutcome>();
        jobs.Ended += (outcome, _) => ended.Add(outcome);

        await jobs.MergeAsync(@"C:\app", "app", new GitHubRepo("acme", "app"), "me", 9, "main", null, () => false, headSha: "old");

        ended.ShouldHaveSingleItem().ChangedGitHub.ShouldBeTrue("the tile's preview holds the old head too");
        ended[0].Reread.ShouldBeTrue();
    }

    [Fact]
    public async Task While_the_list_is_read_again_merge_waits_until_the_new_head_is_there()
    {
        var model = await OpenForMerge("b", head: "old");
        var first = model.PullRequests.LoadAsync(TestContext.Current.CancellationToken); // a slow first read, still on its way
        await Eventually(() => _gh.Asked.Count(a => a == "pr") == 1);

        var again = model.PullRequests.RereadAsync(TestContext.Current.CancellationToken);

        model.PullRequests.IsRereading.ShouldBeTrue();
        model.OpenMergeCommand.Execute(Row(model));
        model.MergeStrip.ShouldBeNull("the rows still hold the old head");
        model.ActionMessage.ShouldBe("Reading the pull requests again. Merge when the list is there.");
        // both reads get gh's answer, which carries the new head (the fake answers every list read alike)
        _gh.PullRequestList.SetResult(PullRequestNine("new"));
        await Task.WhenAll(first, again);
        await Eventually(() => !model.PullRequests.IsRereading && !model.PullRequests.IsLoading);
        await model.PullRequests.Loaded.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Row(model).Item.HeadSha.ShouldBe("new");
        model.OpenMergeCommand.Execute(Row(model));
        model.MergeStrip.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_read_again_before_the_first_load_still_completes_loaded()
    {
        var model = await OpenForMerge("b");

        var again = model.PullRequests.RereadAsync(TestContext.Current.CancellationToken);
        _gh.PullRequestList.SetResult(PullRequestsOf(1));
        await again;

        await model.PullRequests.LoadAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_pull_closes_an_open_strip()
    {
        var model = await OpenForMerge("b");
        model.OpenMergeCommand.Execute(Row(model));

        model.PullCommand.Execute(null);

        model.MergeStrip.ShouldBeNull("its choices were worked out from the branch the pull changes");
        await Eventually(() => !model.IsBusy);
    }

    [Fact]
    public async Task The_strip_stays_under_its_row_when_the_full_list_arrives()
    {
        var model = await OpenForMerge("b");
        model.OpenMergeCommand.Execute(Row(model));
        _gh.PullRequestList.SetResult("""[{"number":9,"title":"Ninth","url":"https://github.com/acme/app/pull/9","updatedAt":"2026-09-23T06:00:00Z","labels":[{"name":"x","color":"ffffff"}],"headRefName":"b","author":{"login":"me"}}]""");
        await model.PullRequests.LoadAsync(TestContext.Current.CancellationToken);

        await StaThread.RunAsync(() =>
        {
            var view = PanelView(model);

            Descendants<TextBlock>(view).Count(t => t.Text == "Merge #9 into main?" && Shown(t, view)).ShouldBe(1, "a new row of the same pull request");
        });
    }

    [Fact]
    public async Task A_strip_button_that_closes_the_strip_hands_the_keyboard_to_its_rows_merge_button()
    {
        var model = await OpenForMerge("b");

        await StaThread.RunAsync(() =>
        {
            var (_, panel, source) = FocusedPanel(model);
            using var _ = source;
            model.OpenMergeCommand.Execute(Row(model));
            panel.UpdateLayout();
            var cancel = Descendants<Button>(panel).Single(b => AutomationProperties.GetName(b) == "Cancel the merge" && Shown(b, panel));
            Keyboard.Focus(cancel).ShouldBe(cancel);

            model.CancelMergeCommand.Execute(null);
            panel.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            AutomationProperties.GetName(Keyboard.FocusedElement.ShouldBeOfType<Button>()).ShouldBe("Merge #9");
        });
    }

    [Fact]
    public async Task Cancel_closes_the_strip_and_merges_nothing()
    {
        var model = await OpenForMerge();
        model.OpenMergeCommand.Execute(Row(model));

        model.CancelMergeCommand.Execute(null);

        model.MergeStrip.ShouldBeNull();
        _merge.Runs.ShouldBeEmpty();
    }

    [Fact]
    public async Task While_a_job_of_the_checkout_runs_merge_opens_nothing()
    {
        var model = await OpenForMerge("feature/x");
        _git.Hold();
        model.PullCommand.Execute(null);

        model.OpenMergeCommand.Execute(Row(model));

        model.MergeStrip.ShouldBeNull();
        _git.Release();
        await Eventually(() => !model.IsBusy);
    }

    [Fact]
    public async Task A_running_merge_names_itself_on_the_button()
    {
        var jobs = MergingJobs();
        var model = await OpenForMerge("b", jobs: jobs);
        _git.Hold();
        model.OpenMergeCommand.Execute(Row(model));

        model.MergeAndPullCommand.Execute(null);

        await Eventually(() => _git.Ran.Count > 0);
        model.PullText.ShouldBe("Merging #9\u2026");
        _git.Release();
        await Eventually(() => !model.IsBusy);
    }

    [Fact]
    public async Task Without_a_merger_there_is_no_merge()
    {
        (await OpenOnLine()).CanMerge.ShouldBeFalse();
        (await OpenForMerge()).CanMerge.ShouldBeTrue();
    }

    [Fact]
    public async Task The_view_puts_merge_on_pull_request_rows_and_the_strip_under_the_row()
    {
        var model = await OpenForMerge("b");
        model.Issues.Rows.ShouldNotBeEmpty();

        await StaThread.RunAsync(() =>
        {
            var view = PanelView(model);
            var merges = Descendants<Button>(view).Where(b => AutomationProperties.GetName(b) == "Merge #9").ToList();
            merges.Count.ShouldBe(1, string.Join(", ", merges.Select(b => b.GetType().Name)));
            var merge = merges[0];
            ((FrameworkElement)merge.Parent).Visibility.ShouldBe(Visibility.Visible);

            model.OpenMergeCommand.Execute(Row(model));
            view.UpdateLayout();

            var strip = Descendants<TextBlock>(view).Single(t => t.Text == "Merge #9 into main?" && Shown(t, view));
            strip.TranslatePoint(new Point(0, 0), view).Y.ShouldBeGreaterThan(merge.TranslatePoint(new Point(0, 0), view).Y, "the strip is under its row");
            var names = Descendants<Button>(view).Where(b => Shown(b, view)).Select(AutomationProperties.GetName).ToList();
            names.ShouldContain("Merge and pull");
            names.ShouldContain("Merge only");
            names.ShouldContain("Cancel the merge");

            model.ShowCommand.Execute(GitHubItemKind.Issue);
            view.UpdateLayout();
            Descendants<Button>(view).Where(b => AutomationProperties.GetName(b) is "Merge #1").ShouldAllBe(b => ((FrameworkElement)b.Parent).Visibility == Visibility.Collapsed);
        });
    }

    /// <summary>The element and every element above it up to <paramref name="root"/> are visible: a strip of another row is collapsed above its text.</summary>
    private static bool Shown(DependencyObject element, DependencyObject root)
    {
        for (var at = element; at is not null && at != root; at = System.Windows.Media.VisualTreeHelper.GetParent(at))
        {
            if (at is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
    }
}
