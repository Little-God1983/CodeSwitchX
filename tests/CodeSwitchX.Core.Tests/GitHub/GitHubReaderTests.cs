using CodeSwitchX.Core.GitHub;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Core.Tests.GitHub;

public class GitHubReaderTests
{
    private static readonly GitHubRepo Repo = new("acme", "tool");

    private sealed class FakeGh(IReadOnlyList<string> accounts, Func<string, IReadOnlyList<string>, string?> answer) : IGhCli
    {
        public IReadOnlyList<string> Accounts => accounts;

        public List<string> Asked { get; } = [];

        public List<IReadOnlyList<string>> Commands { get; } = [];

        /// <summary>What a login's run prints when it exits with an error.</summary>
        public Dictionary<string, string> Printed { get; } = [];

        public Task<IReadOnlyList<string>> AccountsAsync(CancellationToken ct) => Task.FromResult(accounts);

        /// <summary>The commands that were run as list reads.</summary>
        public List<IReadOnlyList<string>> ListCommands { get; } = [];

        /// <summary>A list read that gives nothing ran out of its time.</summary>
        public bool ListTimedOut { get; set; }

        public async Task<GhAnswer> ListAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            ListCommands.Add(arguments);
            var output = await RunAsAsync(login, arguments, ct);
            return new GhAnswer(output, TimedOut: output is null && ListTimedOut);
        }

        public Task<string?> RunAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            Asked.Add(login);
            Commands.Add(arguments);
            return Task.FromResult(answer(login, arguments));
        }

        public async Task<GhAnswer> AnswerAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            var output = await RunAsAsync(login, arguments, ct);
            return new GhAnswer(output, Printed.GetValueOrDefault(login));
        }
    }

    private static string Graphql(string permission, int issues = 0, int pullRequests = 0, string issueNodes = "", string prNodes = "") =>
        "{\"data\":{\"repository\":{\"viewerPermission\":\"" + permission + "\","
        + "\"issues\":{\"totalCount\":" + issues + ",\"nodes\":[" + issueNodes + "]},"
        + "\"pullRequests\":{\"totalCount\":" + pullRequests + ",\"nodes\":[" + prNodes + "]}}}}";

    private static FakeGh Fake(string[] accounts, Dictionary<string, string?> answers) =>
        new(accounts, (login, _) => answers.GetValueOrDefault(login));

    private const string NotFound = """{"data":{"repository":null},"errors":[{"type":"NOT_FOUND","path":["repository"],"message":"Could not resolve to a Repository."}]}""";

    private static Task<RepoRead> Outcome(FakeGh gh, GitHubRepo? repo = null) =>
        new GitHubReader(gh).ReadAsync(repo ?? Repo, gh.Accounts, CancellationToken.None);

    private static async Task<RepoActivity?> Read(FakeGh gh, GitHubRepo? repo = null) => (await Outcome(gh, repo)).Activity;

    [Theory]
    [InlineData("Zed", "acme")]
    [InlineData("acme", "Zed")]
    public async Task Owner_account_reads_its_own_repo_whatever_the_list_order(string first, string second)
    {
        var gh = Fake([first, second], new() { ["acme"] = Graphql("READ"), ["Zed"] = Graphql("ADMIN") });

        var activity = await Read(gh);

        activity!.Account.ShouldBe("acme");
        gh.Asked.ShouldBe(["acme"]);
    }

    [Fact]
    public async Task Otherwise_the_highest_permission_reads_it()
    {
        var gh = Fake(["a", "b"], new() { ["a"] = Graphql("READ"), ["b"] = Graphql("ADMIN") });

        (await Read(gh, new GitHubRepo("other", "tool")))!.Account.ShouldBe("b");
    }

    [Theory]
    [InlineData("Zed", "amy")]
    [InlineData("amy", "Zed")]
    public async Task A_tie_goes_to_the_first_login_alphabetically(string first, string second)
    {
        var gh = Fake([first, second], new() { ["Zed"] = Graphql("READ"), ["amy"] = Graphql("READ") });

        (await Read(gh, new GitHubRepo("other", "tool")))!.Account.ShouldBe("amy");
    }

    [Theory]
    [InlineData("ADMIN", "MAINTAIN")]
    [InlineData("MAINTAIN", "WRITE")]
    [InlineData("WRITE", "TRIAGE")]
    [InlineData("TRIAGE", "READ")]
    public async Task The_order_is_ADMIN_MAINTAIN_WRITE_TRIAGE_READ(string higher, string lower)
    {
        // "a" holds the lower one and sorts first, so only the permission can make "b" win.
        var gh = Fake(["a", "b"], new() { ["a"] = Graphql(lower), ["b"] = Graphql(higher) });

        (await Read(gh, new GitHubRepo("other", "tool")))!.Account.ShouldBe("b");
    }

    [Fact]
    public async Task Falls_through_when_the_owner_account_cannot_see_it()
    {
        var gh = Fake(["acme", "other"], new() { ["acme"] = null, ["other"] = Graphql("READ") });

        (await Read(gh))!.Account.ShouldBe("other");
    }

    [Fact]
    public async Task No_account_sees_it_gives_null()
    {
        var gh = Fake(["a", "b"], new());

        (await Read(gh)).ShouldBeNull();
    }

    [Fact]
    public async Task No_accounts_gives_null()
    {
        (await Read(Fake([], new()))).ShouldBeNull();
    }

    [Fact]
    public async Task A_read_that_failed_is_no_verdict()
    {
        (await Outcome(Fake(["a", "b"], new()))).ShouldBe(RepoRead.Failed);
    }

    [Fact]
    public async Task No_accounts_is_no_verdict()
    {
        (await Outcome(Fake([], new()))).ShouldBe(RepoRead.Failed);
    }

    [Fact]
    public async Task Every_account_saying_not_found_is_a_verdict()
    {
        var gh = Fake(["acme", "other"], new());
        gh.Printed["acme"] = NotFound;
        gh.Printed["other"] = NotFound;

        (await Outcome(gh)).ShouldBe(RepoRead.NotFound);
        gh.Asked.ShouldBe(["acme", "other"]);
    }

    [Theory]
    [InlineData("acme", "other")] // the owner's account says not found, another one failed
    [InlineData("other", "acme")] // the other way round
    public async Task A_failed_account_among_the_not_founds_is_no_verdict(string saysNotFound, string failed)
    {
        var gh = Fake([saysNotFound, failed], new());
        gh.Printed[saysNotFound] = NotFound;

        (await Outcome(gh)).ShouldBe(RepoRead.Failed);
    }

    [Fact]
    public async Task One_account_seeing_it_outweighs_another_saying_not_found()
    {
        var gh = Fake(["acme", "other"], new() { ["other"] = Graphql("READ") });
        gh.Printed["acme"] = NotFound;

        (await Outcome(gh)).Activity!.Account.ShouldBe("other");
    }

    [Theory]
    [InlineData("""{"data":{"repository":null},"errors":[{"type":"FORBIDDEN"}]}""")]
    [InlineData("""{"data":{"repository":null},"errors":[{"type":"NOT_FOUND"},{"type":"RATE_LIMITED"}]}""")]
    [InlineData("""{"data":{"repository":null},"errors":[{"message":"no type"}]}""")]
    [InlineData("""{"data":{"repository":null},"errors":[{"type":5}]}""")]
    [InlineData("""{"data":{"repository":null},"errors":[]}""")]
    [InlineData("""{"data":{"repository":null}}""")]
    [InlineData("""{"data":{"repository":{"viewerPermission":"READ"}},"errors":[{"type":"NOT_FOUND"}]}""")]
    [InlineData("""{"data":null,"errors":[{"type":"NOT_FOUND"}]}""")]
    [InlineData("""{"message":"Bad credentials"}""")]
    [InlineData("[]")]
    [InlineData("502 Bad Gateway")]
    [InlineData("")]
    public async Task Only_GitHubs_not_found_is_a_verdict(string printed)
    {
        var gh = Fake(["acme"], new());
        gh.Printed["acme"] = printed;

        (await Outcome(gh)).ShouldBe(RepoRead.Failed);
    }

    [Fact]
    public async Task Counts_are_the_totals_not_the_rows()
    {
        var rows = string.Join(",", Enumerable.Range(1, 5).Select(i => IssueNode(i, "t" + i)));
        var gh = Fake(["acme"], new() { ["acme"] = Graphql("ADMIN", issues: 19, pullRequests: 7, issueNodes: rows) });

        var activity = await Read(gh);

        activity!.OpenIssues.ShouldBe(19);
        activity.NewestIssues.Count.ShouldBe(5);
        activity.OpenPullRequests.ShouldBe(7);
        activity.NewestPullRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Pr_rows_carry_branch_and_author_and_issue_rows_labels()
    {
        var issue = IssueNode(4, "Fix \\\"quotes\\\" and \\ud83d\\ude80", """{"name":"bug","color":"d73a4a"}""");
        var pr = """
            {"number":9,"title":"Add it","url":"https://github.com/acme/tool/pull/9","updatedAt":"2026-11-01T10:00:00Z",
             "headRefName":"feature/add-it","author":{"login":"amy"},"labels":{"nodes":[]}}
            """;
        var gh = Fake(["acme"], new() { ["acme"] = Graphql("ADMIN", 1, 1, issue, pr) });

        var activity = await Read(gh);

        var issueRow = activity!.NewestIssues.Single();
        issueRow.Number.ShouldBe(4);
        issueRow.Title.ShouldBe("Fix \"quotes\" and \U0001F680");
        issueRow.Url.ShouldBe("https://github.com/acme/tool/issues/4");
        issueRow.UpdatedAt.ShouldBe(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        issueRow.Labels.ShouldBe([new GitHubLabel("bug", "d73a4a")]);
        issueRow.Branch.ShouldBeNull();
        var prRow = activity.NewestPullRequests.Single();
        prRow.Branch.ShouldBe("feature/add-it");
        prRow.Author.ShouldBe("amy");
    }

    [Fact]
    public async Task A_deleted_author_gives_a_null_Author()
    {
        var pr = """
            {"number":9,"title":"Add it","url":"u","updatedAt":"2026-11-01T10:00:00Z",
             "headRefName":"b","author":null,"labels":{"nodes":[]}}
            """;
        var gh = Fake(["acme"], new() { ["acme"] = Graphql("ADMIN", 0, 1, prNodes: pr) });

        (await Read(gh))!.NewestPullRequests.Single().Author.ShouldBeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"data":{"repository":null}}""")]
    [InlineData("""{"errors":[]}""")]
    public async Task Malformed_json_gives_null(string answer)
    {
        var gh = Fake(["acme"], new() { ["acme"] = answer });

        (await Read(gh)).ShouldBeNull();
    }

    [Fact]
    public async Task Query_is_sent_with_string_variables()
    {
        var gh = Fake(["acme"], new() { ["acme"] = Graphql("ADMIN") });

        await Read(gh, new GitHubRepo("acme", "123"));

        var command = gh.Commands.Single();
        command.Take(3).ShouldBe(["api", "graphql", "-f"]);
        command.ShouldContain("owner=acme");
        command.ShouldContain("name=123");
        command.ShouldNotContain("-F");
    }

    [Fact]
    public async Task ListAsync_asks_that_account_with_the_right_command()
    {
        var rows = """
            [{"number":3,"title":"One","url":"u1","updatedAt":"2026-11-01T10:00:00Z","labels":[{"name":"bug","color":"ff0000"}]},
             {"number":2,"title":"Two","url":"u2","updatedAt":"2026-11-02T10:00:00Z","labels":[]}]
            """;
        var gh = new FakeGh([], (_, _) => rows);

        var items = await new GitHubReader(gh).ListAsync(Repo, "amy", GitHubItemKind.Issue, CancellationToken.None);

        gh.Asked.ShouldBe(["amy"]);
        gh.Commands.Single().ShouldBe(["issue", "list", "-R", "acme/tool", "--state", "open", "--limit", "1000", "--json", "number,title,url,updatedAt,labels"]);
        items!.Items.Select(i => i.Number).ShouldBe([3, 2]);
        items.Items[0].Labels.ShouldBe([new GitHubLabel("bug", "ff0000")]);
    }

    [Fact]
    public async Task ListAsync_for_pull_requests_reads_branch_and_author()
    {
        var rows = """
            [{"number":8,"title":"PR","url":"u","updatedAt":"2026-11-01T10:00:00Z","labels":[],"headRefName":"feature/x","author":{"login":"amy"}}]
            """;
        var gh = new FakeGh([], (_, _) => rows);

        var items = await new GitHubReader(gh).ListAsync(Repo, "amy", GitHubItemKind.PullRequest, CancellationToken.None);

        gh.Commands.Single().ShouldBe(["pr", "list", "-R", "acme/tool", "--state", "open", "--limit", "1000", "--json", "number,title,url,updatedAt,labels,headRefName,author"]);
        var item = items.ShouldNotBeNull().Items.Single();
        item.Branch.ShouldBe("feature/x");
        item.Author.ShouldBe("amy");
    }

    [Fact]
    public async Task ListAsync_keeps_a_row_whose_author_has_no_login()
    {
        var rows = """
            [{"number":8,"title":"Odd","url":"u","updatedAt":"2026-11-01T10:00:00Z","labels":[],"headRefName":"feature/x","author":{"name":"Someone"}},
             {"number":7,"title":"Plain","url":"u","updatedAt":"2026-11-01T10:00:00Z","labels":[],"headRefName":"feature/y","author":{"login":"amy"}}]
            """;
        var gh = new FakeGh([], (_, _) => rows);

        var items = (await new GitHubReader(gh).ListAsync(Repo, "amy", GitHubItemKind.PullRequest, CancellationToken.None)).ShouldNotBeNull();

        items.Items.Select(i => i.Author).ShouldBe([null, "amy"]);
    }

    [Theory]
    [InlineData("app/helper-bot", "helper-bot")]
    [InlineData("appsmith", "appsmith")]
    [InlineData("amy", "amy")]
    public async Task ListAsync_spells_an_author_as_GraphQL_does(string login, string expected)
    {
        var rows = "[{\"number\":8,\"title\":\"PR\",\"url\":\"u\",\"updatedAt\":\"2026-11-01T10:00:00Z\",\"labels\":[],\"headRefName\":\"b\","
            + "\"author\":{\"is_bot\":true,\"login\":\"" + login + "\"}}]";
        var gh = new FakeGh([], (_, _) => rows);

        var items = (await new GitHubReader(gh).ListAsync(Repo, "amy", GitHubItemKind.PullRequest, CancellationToken.None)).ShouldNotBeNull();

        items.Items.Single().Author.ShouldBe(expected);
    }

    [Fact]
    public async Task ListAsync_gives_null_when_gh_fails()
    {
        var gh = new FakeGh([], (_, _) => null);

        (await new GitHubReader(gh).ListAsync(Repo, "amy", GitHubItemKind.Issue, CancellationToken.None)).ShouldBeNull();
    }

    private const string GoodRow = """{"number":2,"title":"Fine","url":"u2","updatedAt":"2026-11-02T10:00:00Z","labels":[]}""";

    [Theory]
    [InlineData("""{"number":"1","title":"T","url":"u","updatedAt":"2026-11-01T10:00:00Z","labels":[]}""")]
    [InlineData("""{"number":1.5,"title":"T","url":"u","updatedAt":"2026-11-01T10:00:00Z","labels":[]}""")]
    [InlineData("""{"title":"T","url":"u","updatedAt":"2026-11-01T10:00:00Z","labels":[]}""")]
    [InlineData("""{"number":1,"title":7,"url":"u","updatedAt":"2026-11-01T10:00:00Z","labels":[]}""")]
    [InlineData("""{"number":1,"title":"T","url":null,"updatedAt":"2026-11-01T10:00:00Z","labels":[]}""")]
    [InlineData("""{"number":1,"title":"T","url":"u","updatedAt":null,"labels":[]}""")]
    [InlineData("""{"number":1,"title":"T","url":"u","updatedAt":"last tuesday","labels":[]}""")]
    [InlineData("""{"number":1,"title":"T","url":"u","labels":[]}""")]
    [InlineData("""{"number":1,"title":"T","url":"u","updatedAt":"2026-11-01T10:00:00Z","labels":7}""")]
    [InlineData("""[1,2]""")]
    public async Task ListAsync_skips_an_odd_row_and_keeps_the_others(string odd)
    {
        var gh = new FakeGh([], (_, _) => "[" + odd + "," + GoodRow + "]");

        var list = (await new GitHubReader(gh).ListAsync(Repo, "amy", GitHubItemKind.Issue, CancellationToken.None)).ShouldNotBeNull();

        list.Items.Select(i => i.Number).ShouldBe([2]);
        list.Skipped.ShouldBe(1);
    }

    [Theory]
    [InlineData("""{"number":1,"title":"T","url":"u","updatedAt":"2026-11-01T10:00:00Z","labels":[],"headRefName":4}""")]
    [InlineData("""{"number":1,"title":"T","url":"u","updatedAt":"2026-11-01T10:00:00Z","labels":[]}""")]
    public async Task ListAsync_skips_a_pull_request_with_an_odd_branch(string odd)
    {
        var good = """{"number":2,"title":"Fine","url":"u2","updatedAt":"2026-11-02T10:00:00Z","labels":[],"headRefName":"b"}""";
        var gh = new FakeGh([], (_, _) => "[" + odd + "," + good + "]");

        var list = (await new GitHubReader(gh).ListAsync(Repo, "amy", GitHubItemKind.PullRequest, CancellationToken.None)).ShouldNotBeNull();

        list.Items.Select(i => i.Number).ShouldBe([2]);
        list.Skipped.ShouldBe(1);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"number":1}""")]
    [InlineData("""[{"number":1""")]
    [InlineData("")]
    public async Task ListAsync_gives_null_for_malformed_JSON(string output)
    {
        var gh = new FakeGh([], (_, _) => output);

        (await new GitHubReader(gh).ListAsync(Repo, "amy", GitHubItemKind.Issue, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task ListAsync_is_read_as_a_list_and_the_graphql_read_is_not()
    {
        var gh = new FakeGh(["amy"], (_, args) => args[0] == "api" ? Graphql("ADMIN") : "[]");
        var reader = new GitHubReader(gh);

        await reader.ListAsync(Repo, "amy", GitHubItemKind.Issue, CancellationToken.None);
        await reader.ReadAsync(Repo, ["amy"], CancellationToken.None);

        gh.ListCommands.Single()[0].ShouldBe("issue");
        gh.Commands.Count.ShouldBe(2);
        GhCli.ListTimeout.ShouldBe(TimeSpan.FromSeconds(120));
    }

    private sealed class Lines : ILogger<GitHubReader>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Warnings.Add(logLevel + ": " + formatter(state, exception));
    }

    [Fact]
    public async Task A_list_in_no_readable_format_is_logged_without_names()
    {
        var gh = new FakeGh([], (_, _) => """[{"number":"a"},{"title":1}]""");
        var log = new Lines();

        (await new GitHubReader(gh, log).ListAsync(Repo, "amy", GitHubItemKind.Issue, CancellationToken.None)).ShouldBeNull();

        log.Warnings.ShouldHaveSingleItem().ShouldStartWith("Warning: No row of a GitHub list");
    }

    [Fact]
    public async Task A_read_cancelled_by_the_caller_is_no_failure_and_logs_nothing()
    {
        var gh = new FakeGh([], (_, _) => null);
        var log = new Lines();
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        (await new GitHubReader(gh, log).ListAsync(Repo, "amy", GitHubItemKind.Issue, stop.Token)).ShouldBeNull();

        log.Warnings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true, "ran out of its time")]
    [InlineData(false, "failed")]
    public async Task A_list_read_that_gives_nothing_is_logged_by_why_and_without_names(bool timedOut, string said)
    {
        var gh = new FakeGh([], (_, _) => null) { ListTimedOut = timedOut };
        var log = new Lines();

        (await new GitHubReader(gh, log).ListAsync(Repo, "amy", GitHubItemKind.Issue, CancellationToken.None)).ShouldBeNull();

        var line = log.Warnings.ShouldHaveSingleItem();
        line.ShouldStartWith("Warning: Reading a GitHub list");
        line.ShouldContain(said);
        line.ShouldNotContain("tool");
    }

    [Fact]
    public async Task An_odd_node_in_the_preview_is_skipped_and_the_total_stays()
    {
        var odd = "{\"number\":\"x\",\"title\":\"T\",\"url\":\"u\",\"updatedAt\":null,\"labels\":{\"nodes\":[]}}";
        var gh = Fake(["acme"], new() { ["acme"] = Graphql("ADMIN", issues: 9, issueNodes: odd + "," + IssueNode(4, "Fine")) });

        var activity = (await Read(gh)).ShouldNotBeNull();

        activity.OpenIssues.ShouldBe(9);
        activity.NewestIssues.Select(i => i.Number).ShouldBe([4]);
    }

    [Fact]
    public async Task The_query_asks_for_as_many_as_the_preview_keeps()
    {
        var gh = Fake(["acme"], new() { ["acme"] = Graphql("ADMIN") });

        await Read(gh);

        RepoActivity.PreviewCount.ShouldBe(5);
        var query = gh.Commands.Single().First(a => a.StartsWith("query=", StringComparison.Ordinal));
        query.Split("first:" + RepoActivity.PreviewCount + ",", StringSplitOptions.None).Length.ShouldBe(3);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(999, 0, 999)]
    [InlineData(999, 1, 1500)]
    [InlineData(1000, 0, 1500)]
    [InlineData(500, 3, 503)]
    [InlineData(3, 1, 4)]
    public void A_list_below_the_limit_knows_the_count_skipped_rows_included_and_at_the_limit_keeps_the_known_one(int items, int skipped, int expected)
    {
        var list = new GitHubList(Enumerable.Range(1, items).Select(n => new GitHubItem(n, "t", "u", DateTimeOffset.UnixEpoch, [])).ToList(), skipped);

        list.TotalGiven(known: 1500).ShouldBe(expected);
    }

    [Fact]
    public void A_list_never_totals_below_what_it_shows()
    {
        var list = new GitHubList(Enumerable.Range(1, 1000).Select(n => new GitHubItem(n, "t", "u", DateTimeOffset.UnixEpoch, [])).ToList());

        list.ReachedLimit.ShouldBeTrue();
        list.TotalGiven(known: 40).ShouldBe(1000);
    }

    [Fact]
    public async Task ListAsync_gives_null_when_no_row_can_be_read()
    {
        var gh = new FakeGh([], (_, _) => """[{"number":"a"},{"title":1}]""");

        (await new GitHubReader(gh).ListAsync(Repo, "amy", GitHubItemKind.Issue, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task An_empty_list_is_still_a_list()
    {
        var gh = new FakeGh([], (_, _) => "[]");

        (await new GitHubReader(gh).ListAsync(Repo, "amy", GitHubItemKind.Issue, CancellationToken.None)).ShouldNotBeNull().Items.ShouldBeEmpty();
    }

    private const string OddNode = "{\"number\":\"x\",\"title\":\"T\",\"url\":\"u\",\"updatedAt\":null,\"labels\":{\"nodes\":[]},\"headRefName\":\"b\"}";

    [Fact]
    public async Task A_read_whose_issue_nodes_are_all_odd_is_a_failed_read()
    {
        var gh = Fake(["acme"], new() { ["acme"] = Graphql("ADMIN", issues: 9, issueNodes: OddNode + "," + OddNode) });

        var read = await Outcome(gh);

        read.Activity.ShouldBeNull();
        read.NobodySeesIt.ShouldBeFalse();
    }

    [Fact]
    public async Task A_read_whose_pull_request_nodes_are_all_odd_is_a_failed_read()
    {
        var gh = Fake(["acme"], new() { ["acme"] = Graphql("ADMIN", pullRequests: 2, prNodes: OddNode + "," + OddNode) });

        (await Read(gh)).ShouldBeNull();
    }

    [Fact]
    public async Task A_single_odd_issue_is_a_skipped_one_and_the_pull_requests_stay()
    {
        var gh = Fake(["acme"], new() { ["acme"] = Graphql("ADMIN", issues: 1, issueNodes: OddNode, pullRequests: 1, prNodes: PullRequestNode(7)) });

        var activity = (await Read(gh)).ShouldNotBeNull();

        activity.OpenIssues.ShouldBe(1);
        activity.NewestIssues.ShouldBeEmpty();
        activity.NewestPullRequests.Select(i => i.Number).ShouldBe([7]);
    }

    [Fact]
    public async Task A_single_odd_row_in_a_list_is_a_skipped_one()
    {
        var gh = new FakeGh([], (_, _) => """[{"number":"a"}]""");

        var list = (await new GitHubReader(gh).ListAsync(Repo, "amy", GitHubItemKind.Issue, CancellationToken.None)).ShouldNotBeNull();

        list.Items.ShouldBeEmpty();
        list.Skipped.ShouldBe(1);
        list.TotalGiven(known: 12).ShouldBe(1);
    }

    private static string PullRequestNode(int number) =>
        "{\"number\":" + number + ",\"title\":\"PR\",\"url\":\"u\",\"updatedAt\":\"2026-11-01T10:00:00Z\",\"headRefName\":\"b\",\"labels\":{\"nodes\":[]}}";

    [Theory]
    [InlineData(3, 4, 1, 5)]
    [InlineData(900, 998, 2, 1000)]
    public void A_list_never_totals_below_what_it_read_skipped_rows_included(int known, int items, int skipped, int expected)
    {
        var list = new GitHubList(Enumerable.Range(1, items).Select(n => new GitHubItem(n, "t", "u", DateTimeOffset.UnixEpoch, [])).ToList(), skipped);

        list.TotalGiven(known).ShouldBe(expected);
    }

    [Fact]
    public void SameAs_compares_the_lists_by_content()
    {
        RepoActivity Make(string title) => new(Repo, "acme", 1, 0,
            [new GitHubItem(1, title, "u", DateTimeOffset.UnixEpoch, [new GitHubLabel("bug", "f00")])], []);

        Make("a").SameAs(Make("a")).ShouldBeTrue();
        Make("a").SameAs(Make("b")).ShouldBeFalse();
    }

    private static string IssueNode(int number, string title, string labels = "") =>
        "{\"number\":" + number + ",\"title\":\"" + title + "\",\"url\":\"https://github.com/acme/tool/issues/" + number
        + "\",\"updatedAt\":\"2026-11-01T10:00:00Z\",\"labels\":{\"nodes\":[" + labels + "]}}";
}
