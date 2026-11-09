using CodeSwitchX.Core.GitHub;

namespace CodeSwitchX.Core.Tests.GitHub;

public class GitHubMergerTests
{
    private static readonly GitHubRepo Repo = new("acme", "tool");

    private sealed class FakeGh(GhAnswer answer) : IGhCli
    {
        public List<(string Login, IReadOnlyList<string> Arguments)> Runs { get; } = [];

        public Task<GhAccounts> AccountsAsync(CancellationToken ct) => Task.FromResult(GhAccounts.Of(["me"]));

        public Task<GhAnswer> ListAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            Runs.Add((login, arguments));
            return Task.FromResult(answer);
        }

        public Task<GhAnswer> AnswerAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct) => ListAsAsync(login, arguments, ct);
    }

    [Fact]
    public async Task It_merges_with_a_merge_commit_as_the_reading_account_and_keeps_the_branch()
    {
        var gh = new FakeGh(new GhAnswer(string.Empty));

        var outcome = await new GitHubMerger(gh).MergeAsync(Repo, "reader", 405, "main", null, CancellationToken.None);

        outcome.ShouldBe(new MergeOutcome(true));
        gh.Runs.ShouldHaveSingleItem().Login.ShouldBe("reader");
        gh.Runs[0].Arguments.ShouldBe(["pr", "merge", "405", "-R", "acme/tool", "--merge"]);
    }

    [Fact]
    public async Task It_merges_only_the_head_the_panel_read()
    {
        var gh = new FakeGh(new GhAnswer(string.Empty));

        await new GitHubMerger(gh).MergeAsync(Repo, "reader", 405, "main", "abc123", CancellationToken.None);

        gh.Runs[0].Arguments.ShouldBe(["pr", "merge", "405", "-R", "acme/tool", "--merge", "--match-head-commit", "abc123"]);
    }

    [Fact]
    public async Task A_branch_that_moved_since_the_read_is_refused_and_says_so()
    {
        var gh = new FakeGh(new GhAnswer(null, ErrorOutput: "GraphQL: Head branch was modified. Review and try the merge again. (mergePullRequest)\n"));

        var outcome = await new GitHubMerger(gh).MergeAsync(Repo, "reader", 405, "main", "abc123", CancellationToken.None);

        outcome.ShouldBe(new MergeOutcome(false, "it changed since the panel read it. Look at it, then merge again.", Moved: true));
    }

    [Fact]
    public async Task A_base_branch_that_moved_is_no_moved_head()
    {
        var gh = new FakeGh(new GhAnswer(null, ErrorOutput: "GraphQL: Base branch was modified. Review and try the merge again. (mergePullRequest)\n"));

        var outcome = await new GitHubMerger(gh).MergeAsync(Repo, "reader", 405, "main", "abc123", CancellationToken.None);

        outcome.Moved.ShouldBeFalse();
        outcome.Text.ShouldBe("GraphQL: Base branch was modified. Review and try the merge again. (mergePullRequest)");
    }

    [Theory]
    // what gh prints without a terminal: its strategy warning when it queues, and its warning for one queued before
    [InlineData("! The merge strategy for main is set by the merge queue\n")]
    [InlineData("! Pull request acme/tool#405 is already queued to merge\n")]
    public async Task A_merge_GitHub_only_queued_is_no_merge(string printed)
    {
        var gh = new FakeGh(new GhAnswer(string.Empty, ErrorOutput: printed));

        var outcome = await new GitHubMerger(gh).MergeAsync(Repo, "reader", 405, "main", null, CancellationToken.None);

        outcome.Queued.ShouldBeTrue();
        outcome.Merged.ShouldBeFalse();
        outcome.Text.ShouldBe(printed.Contains("already", StringComparison.Ordinal) ? "is already in the merge queue." : "will be added to the merge queue.");
    }

    [Fact]
    public async Task On_a_queue_repository_the_strategy_warning_is_no_reason()
    {
        var gh = new FakeGh(new GhAnswer(null, ErrorOutput: "! The merge strategy for main is set by the merge queue\nGraphQL: Resource not accessible by integration (enqueuePullRequest)\n"));

        (await new GitHubMerger(gh).MergeAsync(Repo, "reader", 9, "main", null, CancellationToken.None)).Text.ShouldBe("GraphQL: Resource not accessible by integration (enqueuePullRequest)");
    }

    [Fact]
    public async Task A_refusal_gives_GitHubs_reason_in_short()
    {
        var gh = new FakeGh(new GhAnswer(null, ErrorOutput: "X Pull request acme/tool#399 is not mergeable: the merge commit cannot be cleanly created.\nTo have the pull request merged after all the requirements have been met, add the `--auto` flag.\n"));

        var outcome = await new GitHubMerger(gh).MergeAsync(Repo, "reader", 399, "develop", null, CancellationToken.None);

        outcome.ShouldBe(new MergeOutcome(false, "it has conflicts with develop."));
    }

    [Fact]
    public async Task One_merged_already_counts_as_merged()
    {
        var gh = new FakeGh(new GhAnswer(null, ErrorOutput: "X Pull request acme/tool#9 was already merged\n"));

        (await new GitHubMerger(gh).MergeAsync(Repo, "reader", 9, "main", null, CancellationToken.None)).ShouldBe(new MergeOutcome(true, AlreadyMerged: true));
    }

    [Fact]
    public async Task An_empty_stderr_does_not_hide_the_reason_on_stdout()
    {
        var gh = new FakeGh(new GhAnswer(null, FailureOutput: "X Pull request acme/tool#9 is not mergeable: the base branch policy prohibits the merge.\n", ErrorOutput: string.Empty));

        (await new GitHubMerger(gh).MergeAsync(Repo, "reader", 9, "main", null, CancellationToken.None)).Text.ShouldBe("the base branch policy prohibits the merge.");
    }

    [Fact]
    public async Task A_run_out_of_time_says_to_look_on_GitHub()
    {
        var gh = new FakeGh(new GhAnswer(null, TimedOut: true));

        (await new GitHubMerger(gh).MergeAsync(Repo, "reader", 1, "main", null, CancellationToken.None)).Text.ShouldContain("Check the pull request on GitHub");
    }

    [Theory]
    [InlineData("X Pull request acme/tool#9 is not mergeable: the base branch policy prohibits the merge.\n", "the base branch policy prohibits the merge.")]
    [InlineData("GraphQL: Resource not accessible by integration (mergePullRequest)\n", "GraphQL: Resource not accessible by integration (mergePullRequest)")]
    [InlineData("X some other failure\nhint: more\n", "some other failure")]
    [InlineData("", null)]
    public void Reason_is_GitHubs_in_short(string printed, string? reason) => GitHubMerger.Reason(printed, "main").ShouldBe(reason);
}
