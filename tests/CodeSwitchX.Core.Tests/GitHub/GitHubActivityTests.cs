using CodeSwitchX.Core.GitHub;
using CodeSwitchX.Core.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.Core.Tests.GitHub;

public class GitHubActivityTests
{
    private sealed class FakeGh : IGhCli
    {
        private int _running;

        public List<string> Slugs { get; } = [];

        public int MostAtOnce { get; private set; }

        public bool Fail { get; set; }

        public string? HangOn { get; set; }

        public int Issues { get; set; } = 3;

        public TimeSpan Delay { get; set; }

        public bool AccountsThrow { get; set; }

        public int AccountReads { get; private set; }

        public IReadOnlyList<string> Accounts { get; set; } = ["me"];

        /// <summary>What an account's read comes to, instead of the usual answer; null gives the usual one.</summary>
        public Func<string, GhAnswer?>? AnswerOf { get; set; }

        public Task<IReadOnlyList<string>> AccountsAsync(CancellationToken ct)
        {
            lock (Slugs)
            {
                AccountReads++;
            }

            return AccountsThrow ? throw new InvalidOperationException("boom") : Task.FromResult(Accounts);
        }

        public async Task<string?> RunAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct) =>
            (await AnswerAsAsync(login, arguments, ct)).Output;

        public Task<GhAnswer> ListAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct) => AnswerAsAsync(login, arguments, ct);

        public async Task<GhAnswer> AnswerAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            var slug = arguments.First(a => a.StartsWith("owner=", StringComparison.Ordinal)) + "/" + arguments.First(a => a.StartsWith("name=", StringComparison.Ordinal));
            lock (Slugs)
            {
                Slugs.Add(slug);
                MostAtOnce = Math.Max(MostAtOnce, ++_running);
            }

            try
            {
                if (HangOn is not null && slug.EndsWith("name=" + HangOn, StringComparison.Ordinal))
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }

                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, ct);
                }

                return AnswerOf?.Invoke(login) ?? new GhAnswer(Fail ? null : Graphql(Issues));
            }
            finally
            {
                lock (Slugs)
                {
                    _running--;
                }
            }
        }
    }

    private static string Graphql(int issues) =>
        "{\"data\":{\"repository\":{\"viewerPermission\":\"ADMIN\","
        + "\"issues\":{\"totalCount\":" + issues + ",\"nodes\":[]},"
        + "\"pullRequests\":{\"totalCount\":1,\"nodes\":[]}}}}";

    /// <summary>What <c>gh api graphql</c> prints, exiting 1, for a repository the account cannot see.</summary>
    private const string NotFound = """{"data":{"repository":null},"errors":[{"type":"NOT_FOUND","path":["repository"],"message":"Could not resolve to a Repository."}]}""";

    private static GitHubActivity Activity(FakeGh gh, Func<string, string>? originOf = null) =>
        Activity(gh, path => new OriginLookup(originOf is null ? $"https://github.com/acme/{Path.GetFileName(path)}.git" : originOf(path)));

    private static GitHubActivity Activity(FakeGh gh, Func<string, OriginLookup> originOf) =>
        new(gh, new GitHubReader(gh), (path, _) => Task.FromResult(originOf(path)), NullLogger<GitHubActivity>.Instance);

    private static GitHubItem Item(int number) => new(number, "t" + number, "https://github.com/acme/tool/issues/" + number, DateTimeOffset.UnixEpoch, []);

    [Fact]
    public async Task Two_folders_in_one_repo_make_one_read()
    {
        var gh = new FakeGh();
        var activity = Activity(gh, path => path.EndsWith("a", StringComparison.Ordinal) ? "https://github.com/Acme/Tool.git" : "git@github.com:acme/tool.git");

        await activity.RefreshAsync([@"c:\r\a", @"c:\r\b"], CancellationToken.None);

        gh.Slugs.Count.ShouldBe(1);
        activity.ForFolder(@"c:\r\a").ShouldBeSameAs(activity.ForFolder(@"c:\r\b"));
        activity.ForFolder(@"c:\r\a")!.OpenIssues.ShouldBe(3);
    }

    [Fact]
    public async Task A_round_reads_the_accounts_once_however_many_repositories_it_reads()
    {
        var gh = new FakeGh();

        await Activity(gh).RefreshAsync([@"c:\r\one", @"c:\r\two", @"c:\r\three", @"c:\r\four", @"c:\r\five"], CancellationToken.None);

        gh.Slugs.Count.ShouldBe(5);
        gh.AccountReads.ShouldBe(1);
    }

    [Fact]
    public async Task Every_round_reads_the_accounts_again()
    {
        var gh = new FakeGh();
        var activity = Activity(gh);

        await activity.RefreshAsync([@"c:\r\one", @"c:\r\two"], CancellationToken.None);
        await activity.RefreshAsync([@"c:\r\one", @"c:\r\two"], CancellationToken.None);

        gh.AccountReads.ShouldBe(2);
    }

    [Fact]
    public async Task A_round_with_no_GitHub_repository_reads_no_accounts()
    {
        var gh = new FakeGh();

        await Activity(gh, _ => "https://example.com/acme/tool.git").RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        gh.AccountReads.ShouldBe(0);
    }

    [Fact]
    public async Task Accounts_that_cannot_be_read_keep_the_last_result()
    {
        var gh = new FakeGh();
        var activity = Activity(gh);
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);
        var first = activity.ForFolder(@"c:\r\tool");
        first.ShouldNotBeNull();
        gh.AccountsThrow = true;

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.ForFolder(@"c:\r\tool").ShouldBeSameAs(first);
    }

    [Fact]
    public async Task A_failed_read_keeps_the_last_result()
    {
        var gh = new FakeGh();
        var activity = Activity(gh);
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);
        var first = activity.ForFolder(@"c:\r\tool");
        gh.Fail = true;

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.ForFolder(@"c:\r\tool").ShouldBeSameAs(first);
    }

    [Fact]
    public async Task A_real_zero_replaces_the_last_result()
    {
        var gh = new FakeGh();
        var activity = Activity(gh);
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);
        gh.Issues = 0;

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.ForFolder(@"c:\r\tool")!.OpenIssues.ShouldBe(0);
    }

    [Fact]
    public async Task Nothing_new_keeps_the_same_instance_and_stays_quiet()
    {
        var gh = new FakeGh();
        var activity = Activity(gh);
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);
        var first = activity.ForFolder(@"c:\r\tool");
        var changes = 0;
        activity.Changed += (_, _) => changes++;

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.ForFolder(@"c:\r\tool").ShouldBeSameAs(first);
        changes.ShouldBe(0);
    }

    [Fact]
    public async Task A_change_fires_Changed_once_for_the_round()
    {
        var gh = new FakeGh();
        var activity = Activity(gh);
        var changes = 0;
        activity.Changed += (_, _) => changes++;

        await activity.RefreshAsync([@"c:\r\tool", @"c:\r\other"], CancellationToken.None);
        changes.ShouldBe(1, "two repositories came in, one event");
        gh.Issues = 9;
        await activity.RefreshAsync([@"c:\r\tool", @"c:\r\other"], CancellationToken.None);

        changes.ShouldBe(2);
    }

    [Fact]
    public async Task A_folder_without_a_github_origin_is_known_and_has_nothing()
    {
        var gh = new FakeGh();
        var activity = Activity(gh, _ => "https://example.com/acme/tool.git");
        activity.Knows(@"c:\r\tool").ShouldBeFalse();

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.Knows(@"c:\r\tool").ShouldBeTrue();
        activity.ForFolder(@"c:\r\tool").ShouldBeNull();
        gh.Slugs.ShouldBeEmpty();
    }

    [Fact]
    public async Task No_more_than_four_reads_run_at_once()
    {
        var gh = new FakeGh { Delay = TimeSpan.FromMilliseconds(30) };
        var activity = Activity(gh);

        await activity.RefreshAsync(Enumerable.Range(0, 10).Select(i => @"c:\r\repo" + i).ToList(), CancellationToken.None);

        gh.Slugs.Count.ShouldBe(10);
        gh.MostAtOnce.ShouldBe(4);
    }

    [Fact]
    public async Task Replace_sets_the_count_and_the_five_newest()
    {
        var gh = new FakeGh();
        var activity = Activity(gh, _ => "https://github.com/acme/tool.git");
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);
        var before = activity.ForFolder(@"c:\r\tool")!;
        var changes = 0;
        activity.Changed += (_, _) => changes++;

        activity.Replace(new GitHubRepo("acme", "tool"), GitHubItemKind.Issue, new GitHubList(Enumerable.Range(1, 7).Select(Item).ToList()), before, out _);

        var after = activity.ForFolder(@"c:\r\tool")!;
        after.OpenIssues.ShouldBe(7);
        after.NewestIssues.Select(i => i.Number).ShouldBe([1, 2, 3, 4, 5]);
        after.OpenPullRequests.ShouldBe(before.OpenPullRequests);
        after.NewestPullRequests.ShouldBe(before.NewestPullRequests);
        changes.ShouldBe(1);
    }

    private static GitHubList ListOf(int items, int skipped = 0) =>
        new(Enumerable.Range(1, items).Select(Item).ToList(), skipped);

    /// <summary>A repository GraphQL counts 1500 open issues of.</summary>
    private static async Task<(GitHubActivity Activity, GitHubRepo Repo)> ReadOf1500Async()
    {
        var gh = new FakeGh { Issues = 1500 };
        var activity = Activity(gh, _ => "https://github.com/acme/tool.git");
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);
        return (activity, new GitHubRepo("acme", "tool"));
    }

    [Theory]
    [InlineData(7, 0, 7)]
    [InlineData(999, 0, 999)]
    [InlineData(1000, 0, 1500)]
    [InlineData(998, 2, 1500)]
    [InlineData(7, 1, 8)]
    public async Task Replace_keeps_the_graphql_total_unless_the_list_is_whole(int items, int skipped, int expected)
    {
        var (activity, repo) = await ReadOf1500Async();

        activity.Replace(repo, GitHubItemKind.Issue, ListOf(items, skipped), activity.Latest(repo), out _).ShouldBeTrue();

        var after = activity.ForFolder(@"c:\r\tool")!;
        after.OpenIssues.ShouldBe(expected);
        after.NewestIssues.Select(i => i.Number).ShouldBe(Enumerable.Range(1, Math.Min(5, items)));
    }

    [Fact]
    public async Task A_list_started_before_a_newer_result_is_ignored_and_the_stored_total_comes_back()
    {
        var gh = new FakeGh { Issues = 3 };
        var activity = Activity(gh, _ => "https://github.com/acme/tool.git");
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);
        var repo = new GitHubRepo("acme", "tool");
        var started = activity.Latest(repo);
        gh.Issues = 20;
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);
        var before = activity.ForFolder(@"c:\r\tool")!;

        activity.Replace(repo, GitHubItemKind.Issue, ListOf(7), started, out _).ShouldBeFalse();
        activity.ForFolder(@"c:\r\tool").ShouldBeSameAs(before);
    }

    [Fact]
    public async Task A_list_started_after_the_latest_result_is_applied_and_its_total_comes_back()
    {
        var gh = new FakeGh { Issues = 3 };
        var activity = Activity(gh, _ => "https://github.com/acme/tool.git");
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);
        var repo = new GitHubRepo("acme", "tool");

        activity.Replace(repo, GitHubItemKind.Issue, ListOf(7), activity.Latest(repo), out var total).ShouldBeTrue();

        total.ShouldBe(7);
        activity.ForFolder(@"c:\r\tool")!.OpenIssues.ShouldBe(7);
    }

    [Fact]
    public void A_repository_no_round_has_read_has_no_total_to_give()
    {
        var activity = Activity(new FakeGh(), _ => "https://github.com/acme/tool.git");

        activity.Replace(new GitHubRepo("acme", "tool"), GitHubItemKind.Issue, ListOf(2), null, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_list_read_when_nothing_was_stored_does_not_overwrite_what_was_stored_during_the_read()
    {
        var activity = Activity(new FakeGh { Issues = 3 }, _ => "https://github.com/acme/tool.git");
        var repo = new GitHubRepo("acme", "tool");
        var started = activity.Latest(repo); // null
        await activity.RefreshAsync([@"c:	ool"], CancellationToken.None);

        activity.Replace(repo, GitHubItemKind.Issue, ListOf(7), started, out _).ShouldBeFalse();

        activity.ForFolder(@"c:	ool")!.OpenIssues.ShouldBe(3);
    }

    [Fact]
    public async Task A_slow_repository_does_not_hold_back_the_others_results()
    {
        var gh = new FakeGh { HangOn = "slow" };
        var activity = Activity(gh);
        using var stop = new CancellationTokenSource();

        var round = activity.RefreshAsync([@"c:\r\slow", @"c:\r\fast"], stop.Token);

        var until = DateTime.UtcNow.AddSeconds(10);
        while (activity.ForFolder(@"c:\r\fast") is null && DateTime.UtcNow < until)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        activity.ForFolder(@"c:\r\fast").ShouldNotBeNull();
        round.IsCompleted.ShouldBeFalse();
        await stop.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(round);
    }

    [Theory]
    [InlineData(true, 4242)] // git timed out
    [InlineData(false, null)] // git did not start
    public async Task A_failed_origin_lookup_keeps_the_repository_the_folder_had(bool timedOut, int? pid)
    {
        var gh = new FakeGh();
        var origin = new OriginLookup("https://github.com/acme/tool.git");
        var activity = Activity(gh, _ => origin);
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);
        var first = activity.ForFolder(@"c:\r\tool");
        first.ShouldNotBeNull();
        var changes = 0;
        activity.Changed += (_, _) => changes++;
        origin = OriginLookup.From(new ProcessRunResult(null, timedOut, pid));

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.ForFolder(@"c:\r\tool").ShouldBeSameAs(first);
        changes.ShouldBe(0);
    }

    [Fact]
    public async Task A_failed_first_lookup_leaves_the_folder_unknown_for_the_next_round()
    {
        var gh = new FakeGh();
        var origin = OriginLookup.Failure;
        var activity = Activity(gh, _ => origin);

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.Knows(@"c:\r\tool").ShouldBeFalse();
        origin = new OriginLookup("https://github.com/acme/tool.git");

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.Knows(@"c:\r\tool").ShouldBeTrue();
        activity.ForFolder(@"c:\r\tool")!.Repo.ShouldBe(new GitHubRepo("acme", "tool"));
    }

    [Fact]
    public async Task An_origin_that_is_gone_clears_the_repository_and_tells_the_tiles()
    {
        var gh = new FakeGh();
        var origin = new OriginLookup("https://github.com/acme/tool.git");
        var activity = Activity(gh, _ => origin);
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);
        activity.ForFolder(@"c:\r\tool").ShouldNotBeNull();
        var changes = 0;
        activity.Changed += (_, _) => changes++;
        origin = OriginLookup.NoOrigin;

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.ForFolder(@"c:\r\tool").ShouldBeNull();
        activity.Knows(@"c:\r\tool").ShouldBeTrue();
        changes.ShouldBe(1);
    }

    [Fact]
    public async Task A_folder_losing_its_origin_leaves_another_folder_in_that_repository_alone()
    {
        var gh = new FakeGh();
        var origin = new OriginLookup("https://github.com/acme/tool.git");
        var activity = Activity(gh, path => path.EndsWith('a') ? origin : new OriginLookup("https://github.com/acme/tool.git"));
        await activity.RefreshAsync([@"c:\r\a", @"c:\r\b"], CancellationToken.None);
        origin = OriginLookup.NoOrigin;

        await activity.RefreshAsync([@"c:\r\a", @"c:\r\b"], CancellationToken.None);

        activity.ForFolder(@"c:\r\a").ShouldBeNull();
        activity.ForFolder(@"c:\r\b").ShouldNotBeNull();
    }

    [Fact]
    public async Task A_folder_moved_to_a_repository_already_read_tells_the_tiles()
    {
        var gh = new FakeGh();
        var origin = new OriginLookup("https://github.com/acme/tool.git");
        var activity = Activity(gh, path => path.EndsWith('a') ? origin : new OriginLookup("https://github.com/acme/other.git"));
        await activity.RefreshAsync([@"c:\r\a", @"c:\r\b"], CancellationToken.None);
        var changes = 0;
        activity.Changed += (_, _) => changes++;
        origin = new OriginLookup("https://github.com/acme/other.git");

        await activity.RefreshAsync([@"c:\r\a", @"c:\r\b"], CancellationToken.None);

        activity.ForFolder(@"c:\r\a").ShouldBeSameAs(activity.ForFolder(@"c:\r\b"));
        changes.ShouldBe(1);
    }

    [Fact]
    public async Task A_repository_every_account_says_is_not_there_loses_its_result()
    {
        var gh = new FakeGh { Accounts = ["me", "other"] };
        var activity = Activity(gh);
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);
        activity.ForFolder(@"c:\r\tool").ShouldNotBeNull();
        var changes = 0;
        activity.Changed += (_, _) => changes++;
        gh.AnswerOf = _ => new GhAnswer(null, NotFound);

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.ForFolder(@"c:\r\tool").ShouldBeNull();
        changes.ShouldBe(1);

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        changes.ShouldBe(1, "nothing left to lose, nothing to tell");
    }

    [Fact]
    public async Task A_repository_that_comes_back_shows_again()
    {
        var gh = new FakeGh();
        var activity = Activity(gh);
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);
        gh.AnswerOf = _ => new GhAnswer(null, NotFound);
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);
        gh.AnswerOf = null;

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.ForFolder(@"c:\r\tool").ShouldNotBeNull();
    }

    [Theory]
    [InlineData("me")]
    [InlineData("other")]
    public async Task One_account_not_found_and_one_failed_keeps_the_last_result(string failing)
    {
        var gh = new FakeGh { Accounts = ["me", "other"] };
        var activity = Activity(gh);
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);
        var first = activity.ForFolder(@"c:\r\tool");
        first.ShouldNotBeNull();
        gh.AnswerOf = login => login == failing ? new GhAnswer(null) : new GhAnswer(null, NotFound);

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.ForFolder(@"c:\r\tool").ShouldBeSameAs(first);
    }

    [Fact]
    public async Task No_accounts_at_all_keeps_the_last_result()
    {
        var gh = new FakeGh();
        var activity = Activity(gh);
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);
        var first = activity.ForFolder(@"c:\r\tool");
        first.ShouldNotBeNull();
        gh.Accounts = [];

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.ForFolder(@"c:\r\tool").ShouldBeSameAs(first);
    }

    [Fact]
    public async Task A_folder_not_retained_is_unknown_and_its_repository_result_goes_with_it()
    {
        var gh = new FakeGh();
        var activity = Activity(gh);
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.Retain([]);

        activity.Knows(@"c:\r\tool").ShouldBeFalse();
        activity.ForFolder(@"c:\r\tool").ShouldBeNull();
        activity.Latest(new GitHubRepo("acme", "tool")).ShouldBeNull();
    }

    [Fact]
    public async Task A_repository_keeps_its_result_while_a_retained_folder_refers_to_it()
    {
        var gh = new FakeGh();
        var activity = Activity(gh, _ => "https://github.com/acme/tool.git");
        await activity.RefreshAsync([@"c:\r\a", @"c:\r\b"], CancellationToken.None);

        activity.Retain([@"c:\r\b"]);

        activity.Knows(@"c:\r\a").ShouldBeFalse();
        activity.ForFolder(@"c:\r\b").ShouldNotBeNull();
    }

    [Fact]
    public async Task A_folder_is_retained_with_or_without_a_trailing_separator()
    {
        var gh = new FakeGh();
        var activity = Activity(gh);
        await activity.RefreshAsync([@"c:\r\x"], CancellationToken.None);

        activity.Retain([@"c:\r\x\"]);

        activity.Knows(@"c:\r\x").ShouldBeTrue();
        activity.ForFolder(@"c:\r\x").ShouldNotBeNull();
    }

    [Fact]
    public async Task The_old_result_of_a_moved_origin_goes_when_no_folder_refers_to_it_any_more()
    {
        var gh = new FakeGh();
        var origin = new OriginLookup("https://github.com/acme/tool.git");
        var activity = Activity(gh, _ => origin);
        await activity.RefreshAsync([@"c:\r\a"], CancellationToken.None);
        origin = new OriginLookup("https://github.com/acme/other.git");
        await activity.RefreshAsync([@"c:\r\a"], CancellationToken.None);

        activity.Retain([@"c:\r\a"]);

        activity.Latest(new GitHubRepo("acme", "tool")).ShouldBeNull();
        activity.ForFolder(@"c:\r\a")!.Repo.ShouldBe(new GitHubRepo("acme", "other"));
    }

    [Fact]
    public async Task A_folder_added_again_after_it_was_dropped_is_read_afresh()
    {
        var gh = new FakeGh { Issues = 3 };
        var activity = Activity(gh);
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);
        activity.Retain([]);
        gh.Issues = 8;

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        gh.Slugs.Count.ShouldBe(2);
        activity.ForFolder(@"c:\r\tool")!.OpenIssues.ShouldBe(8);
    }

    [Fact]
    public async Task A_new_folder_in_a_repository_already_read_tells_the_tiles()
    {
        var gh = new FakeGh();
        var activity = Activity(gh, _ => "https://github.com/acme/tool.git");
        await activity.RefreshAsync([@"c:\r\a"], CancellationToken.None);
        var changes = 0;
        activity.Changed += (_, _) => changes++;

        await activity.RefreshAsync([@"c:\r\a", @"c:\r\b"], CancellationToken.None);

        activity.ForFolder(@"c:\r\b").ShouldBeSameAs(activity.ForFolder(@"c:\r\a"));
        changes.ShouldBe(1);
    }
}
