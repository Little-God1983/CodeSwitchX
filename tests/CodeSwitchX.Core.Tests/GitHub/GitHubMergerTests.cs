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

        var outcome = await new GitHubMerger(gh).MergeAsync(Repo, "reader", 405, "main", CancellationToken.None);

        outcome.ShouldBe(new MergeOutcome(true));
        gh.Runs.ShouldHaveSingleItem().Login.ShouldBe("reader");
        gh.Runs[0].Arguments.ShouldBe(["pr", "merge", "405", "-R", "acme/tool", "--merge"]);
    }

    [Fact]
    public async Task A_refusal_gives_GitHubs_reason_in_short()
    {
        var gh = new FakeGh(new GhAnswer(null, ErrorOutput: "X Pull request acme/tool#399 is not mergeable: the merge commit cannot be cleanly created.\nTo have the pull request merged after all the requirements have been met, add the `--auto` flag.\n"));

        var outcome = await new GitHubMerger(gh).MergeAsync(Repo, "reader", 399, "develop", CancellationToken.None);

        outcome.ShouldBe(new MergeOutcome(false, "it has conflicts with develop."));
    }

    [Fact]
    public async Task A_run_out_of_time_says_to_look_on_GitHub()
    {
        var gh = new FakeGh(new GhAnswer(null, TimedOut: true));

        (await new GitHubMerger(gh).MergeAsync(Repo, "reader", 1, "main", CancellationToken.None)).Text.ShouldContain("Check the pull request on GitHub");
    }

    [Theory]
    [InlineData("X Pull request acme/tool#9 is not mergeable: the base branch policy prohibits the merge.\n", "the base branch policy prohibits the merge.")]
    [InlineData("X Pull request acme/tool#9 was already merged\n", "it was merged already.")]
    [InlineData("GraphQL: Resource not accessible by integration (mergePullRequest)\n", "GraphQL: Resource not accessible by integration (mergePullRequest)")]
    [InlineData("X some other failure\nhint: more\n", "some other failure")]
    [InlineData("", null)]
    public void Reason_is_GitHubs_in_short(string printed, string? reason) => GitHubMerger.Reason(printed, "main").ShouldBe(reason);
}
