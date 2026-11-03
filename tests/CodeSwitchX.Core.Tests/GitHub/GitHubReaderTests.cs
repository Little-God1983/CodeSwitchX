using CodeSwitchX.Core.GitHub;

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
        items!.Select(i => i.Number).ShouldBe([3, 2]);
        items![0].Labels.ShouldBe([new GitHubLabel("bug", "ff0000")]);
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
        var item = items.ShouldNotBeNull().Single();
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

        items.Select(i => i.Author).ShouldBe([null, "amy"]);
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

        items.Single().Author.ShouldBe(expected);
    }

    [Fact]
    public async Task ListAsync_gives_null_when_gh_fails()
    {
        var gh = new FakeGh([], (_, _) => null);

        (await new GitHubReader(gh).ListAsync(Repo, "amy", GitHubItemKind.Issue, CancellationToken.None)).ShouldBeNull();
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
