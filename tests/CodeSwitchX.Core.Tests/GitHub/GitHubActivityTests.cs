using CodeSwitchX.Core.GitHub;
using CodeSwitchX.Core.Workspaces;
using Microsoft.Extensions.Logging;
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

        public bool AccountsNull { get; set; }

        public GhFailure AccountsFailure { get; set; } = GhFailure.Other;

        public int AccountReads { get; private set; }

        public IReadOnlyList<string> Accounts { get; set; } = ["me"];

        /// <summary>What an account's read comes to, instead of the usual answer; null gives the usual one.</summary>
        public Func<string, GhAnswer?>? AnswerOf { get; set; }

        public Task<GhAccounts> AccountsAsync(CancellationToken ct)
        {
            lock (Slugs)
            {
                AccountReads++;
            }

            return AccountsThrow ? throw new InvalidOperationException("boom") : Task.FromResult(AccountsNull ? GhAccounts.Failed(AccountsFailure) : GhAccounts.Of(Accounts));
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

    private static GitHubActivity Activity(FakeGh gh, Func<string, OriginLookup> originOf, ILogger<GitHubActivity>? logger = null, Func<string, CancellationToken, Task<string?>>? hostOfAlias = null) =>
        new(gh, new GitHubReader(gh), (path, _) => Task.FromResult(originOf(path)), logger ?? NullLogger<GitHubActivity>.Instance, hostOfAlias);

    private sealed class Lines : ILogger<GitHubActivity>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IEnumerable<string> Warnings
        {
            get
            {
                lock (Entries)
                {
                    return Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Message).ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }

    private static readonly string[] ThreeFolders = [@"c:\r\one", @"c:\r\two", @"c:\r\three"];

    private static GitHubItem Item(int number) => new(number, "t" + number, "https://github.com/acme/tool/issues/" + number, DateTimeOffset.UnixEpoch, []);

    [Fact]
    public async Task A_round_that_read_every_repository_comes_back_true()
    {
        (await Activity(new FakeGh()).RefreshAsync(ThreeFolders, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_round_with_a_read_that_failed_comes_back_false()
    {
        (await Activity(new FakeGh { Fail = true }).RefreshAsync(ThreeFolders, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_round_whose_accounts_could_not_be_read_comes_back_false()
    {
        (await Activity(new FakeGh { AccountsNull = true }).RefreshAsync(ThreeFolders, CancellationToken.None)).ShouldBeFalse();
        (await Activity(new FakeGh { AccountsThrow = true }).RefreshAsync(ThreeFolders, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_round_is_not_failed_by_a_repository_nobody_can_see()
    {
        var gh = new FakeGh { AnswerOf = _ => new GhAnswer(null, NotFound) };

        (await Activity(gh).RefreshAsync(ThreeFolders, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_round_is_not_failed_by_a_folder_git_could_not_answer_for()
    {
        var round = Activity(new FakeGh(), _ => OriginLookup.Failure).RefreshAsync(ThreeFolders, CancellationToken.None);

        (await round).ShouldBeTrue("that is git's answer, and GitHub was not asked");
    }

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
    public async Task A_failed_accounts_read_makes_no_reads_and_keeps_the_last_result()
    {
        var gh = new FakeGh();
        var activity = Activity(gh);
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);
        var first = activity.ForFolder(@"c:\r\tool");
        first.ShouldNotBeNull();
        var asked = gh.Slugs.Count;
        gh.AccountsNull = true;

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        gh.Slugs.Count.ShouldBe(asked, "no repository was read");
        activity.ForFolder(@"c:\r\tool").ShouldBeSameAs(first);
    }

    [Fact]
    public async Task A_round_where_every_read_fails_logs_one_warning()
    {
        var gh = new FakeGh { Fail = true };
        var log = new Lines();

        await Activity(gh, AcmeRepoOf, log).RefreshAsync(ThreeFolders, CancellationToken.None);

        log.Warnings.ShouldHaveSingleItem().ShouldContain("3 of 3 repositories");
        log.Entries.Count(e => e.Level == LogLevel.Debug).ShouldBe(3, "each repository is told at Debug");
    }

    [Fact]
    public async Task A_round_with_no_accounts_logs_one_warning_not_one_per_repository()
    {
        var gh = new FakeGh { Accounts = [] };
        var log = new Lines();

        await Activity(gh, AcmeRepoOf, log).RefreshAsync(ThreeFolders, CancellationToken.None);

        log.Warnings.ShouldHaveSingleItem().ShouldContain("3 of 3 repositories");
    }

    [Fact]
    public async Task A_round_where_some_reads_fail_says_how_many()
    {
        var reads = 0;
        var gh = new FakeGh { AnswerOf = _ => Interlocked.Increment(ref reads) == 1 ? new GhAnswer(Graphql(3)) : new GhAnswer(null) };
        var log = new Lines();

        await Activity(gh, AcmeRepoOf, log).RefreshAsync(ThreeFolders, CancellationToken.None);

        log.Warnings.ShouldHaveSingleItem().ShouldContain("2 of 3 repositories");
    }

    private static OriginLookup AcmeRepoOf(string path) => new($"https://github.com/acme/{Path.GetFileName(path)}.git");

    [Fact]
    public async Task Warnings_of_a_round_never_carry_a_folder_or_a_repository()
    {
        var gh = new FakeGh { Fail = true };
        var log = new Lines();

        await Activity(gh, AcmeRepoOf, log).RefreshAsync(ThreeFolders, CancellationToken.None);

        log.Entries.Where(e => e.Level >= LogLevel.Warning).ShouldAllBe(e => !e.Message.Contains("one") && !e.Message.Contains("acme"));
    }

    [Theory]
    [InlineData(GhFailure.NotFound)]
    [InlineData(GhFailure.TooOld)]
    [InlineData(GhFailure.Other)]
    public async Task An_accounts_read_that_failed_is_logged_once_with_its_kind(GhFailure kind)
    {
        var gh = new FakeGh { AccountsNull = true, AccountsFailure = kind };
        var log = new Lines();

        await Activity(gh, AcmeRepoOf, log).RefreshAsync(ThreeFolders, CancellationToken.None);

        log.Warnings.ShouldHaveSingleItem().ShouldContain(kind.ToString());
    }

    [Fact]
    public async Task An_exception_is_logged_by_its_type_and_never_with_its_text()
    {
        var gh = new FakeGh { AccountsThrow = true };
        var log = new Lines();
        var activity = Activity(gh, path => path.EndsWith("two", StringComparison.Ordinal) ? throw new InvalidOperationException("secret title") : new OriginLookup("https://github.com/acme/one.git"), log);

        await activity.RefreshAsync([@"c:\r\one", @"c:\r\two"], CancellationToken.None);

        log.Entries.ShouldAllBe(e => e.Exception == null && !e.Message.Contains("secret"));
        log.Warnings.ShouldContain(m => m.Contains(nameof(InvalidOperationException), StringComparison.Ordinal) && m.Contains("   at ", StringComparison.Ordinal), "the stack is there");
    }

    [Fact]
    public async Task A_read_that_throws_is_logged_by_its_type_once_for_the_round()
    {
        var gh = new FakeGh { AnswerOf = _ => throw new InvalidOperationException("secret title") };
        var log = new Lines();

        await Activity(gh, AcmeRepoOf, log).RefreshAsync(ThreeFolders, CancellationToken.None);

        log.Entries.ShouldAllBe(e => e.Exception == null && !e.Message.Contains("secret"));
        log.Warnings.ShouldHaveSingleItem().ShouldContain("3 of 3 repositories");
        log.Warnings.Single().ShouldContain(nameof(InvalidOperationException));
        log.Warnings.Single().ShouldContain("   at ", Case.Sensitive);
    }

    [Fact]
    public async Task An_alias_remote_gets_a_repository_when_ssh_says_it_is_GitHub()
    {
        var gh = new FakeGh();
        var asked = new List<string>();
        var activity = Activity(gh, _ => new OriginLookup("git@github-work:acme/tool.git"), null, (alias, _) =>
        {
            asked.Add(alias);
            return Task.FromResult<string?>("github.com");
        });

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.ForFolder(@"c:\r\tool").ShouldNotBeNull();
        gh.Slugs.Single().ShouldEndWith("name=tool");
        asked.ShouldBe(["github-work"]);
    }

    [Theory]
    [InlineData("gitlab.com")]
    [InlineData("github.com-evil.example")]
    [InlineData("github.com.evil.example")]
    [InlineData(null)]
    public async Task An_alias_remote_that_is_not_GitHub_gets_nothing(string? resolved)
    {
        var gh = new FakeGh();
        var activity = Activity(gh, _ => new OriginLookup("git@github-work:acme/tool.git"), null, (_, _) => Task.FromResult(resolved));

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.ForFolder(@"c:\r\tool").ShouldBeNull();
        activity.Knows(@"c:\r\tool").ShouldBeTrue();
        gh.Slugs.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_alias_that_cannot_be_resolved_does_not_break_the_round()
    {
        var gh = new FakeGh();
        var log = new Lines();
        var activity = Activity(gh, path => new OriginLookup(path.EndsWith("alias", StringComparison.Ordinal) ? "git@github-work:acme/tool.git" : "https://github.com/acme/other.git"), log, (_, _) => throw new InvalidOperationException("secret"));

        await activity.RefreshAsync([@"c:\r\alias", @"c:\r\other"], CancellationToken.None);

        activity.ForFolder(@"c:\r\other").ShouldNotBeNull();
        activity.ForFolder(@"c:\r\alias").ShouldBeNull();
        log.Entries.ShouldAllBe(e => e.Exception == null && !e.Message.Contains("secret"));
    }

    [Fact]
    public async Task The_alias_is_asked_as_written()
    {
        var asked = new List<string>();
        var activity = Activity(new FakeGh(), _ => new OriginLookup("ssh://git@GitHub-Work/acme/tool.git"), null, (alias, _) =>
        {
            asked.Add(alias);
            return Task.FromResult<string?>("github.com");
        });

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        asked.ShouldBe(["GitHub-Work"]);
    }

    [Theory]
    [InlineData("git.corp.example", false)]
    [InlineData("github.com", true)]
    [InlineData(null, true)] // ssh has nothing to say: the convention counts
    public async Task A_github_com_dash_name_host_is_checked_with_ssh(string? resolved, bool pills)
    {
        var activity = Activity(new FakeGh(), _ => new OriginLookup("git@github.com-corp:acme/tool.git"), null, (_, _) => Task.FromResult(resolved));

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        (activity.ForFolder(@"c:\r\tool") is not null).ShouldBe(pills);
    }

    [Fact]
    public async Task A_failed_ssh_keeps_the_repository_the_folder_had()
    {
        var gh = new FakeGh();
        var remote = "https://github.com/acme/tool.git";
        var activity = Activity(gh, _ => new OriginLookup(remote), null, (_, _) => Task.FromResult<string?>(null));
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);
        var first = activity.ForFolder(@"c:\r\tool").ShouldNotBeNull();
        remote = "git@github-work:acme/other.git";

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.ForFolder(@"c:\r\tool").ShouldBeSameAs(first);
    }

    [Fact]
    public async Task Origins_that_could_not_be_read_are_one_warning_per_round_without_the_paths()
    {
        var log = new Lines();
        var activity = Activity(new FakeGh(), _ => OriginLookup.Failure, log);

        await activity.RefreshAsync(ThreeFolders, CancellationToken.None);

        log.Warnings.ShouldHaveSingleItem().ShouldContain("3 of 3 folders");
        log.Entries.Where(e => e.Level >= LogLevel.Warning).ShouldAllBe(e => !e.Message.Contains("one") && !e.Message.Contains("c:"));
        log.Entries.Count(e => e.Level == LogLevel.Debug && e.Message.Contains("three")).ShouldBe(1, "the path is for Debug");
    }

    [Fact]
    public async Task A_dash_name_host_keeps_its_pills_when_ssh_failed()
    {
        var activity = Activity(new FakeGh(), _ => new OriginLookup("git@github.com-corp:acme/tool.git"), null, (_, _) => Task.FromResult<string?>(null));

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.ForFolder(@"c:\r\tool").ShouldNotBeNull();
    }

    [Fact]
    public async Task An_alias_with_no_answer_is_recorded_as_no_repository_and_is_no_failure()
    {
        var log = new Lines();
        var activity = Activity(new FakeGh(), _ => new OriginLookup("git@github-work:acme/tool.git"), log, (_, _) => Task.FromResult<string?>(null));

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.Knows(@"c:\r\tool").ShouldBeTrue();
        activity.ForFolder(@"c:\r\tool").ShouldBeNull();
        log.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_echo_from_ssh_clears_the_repository_the_folder_had()
    {
        var remote = "https://github.com/acme/tool.git";
        var activity = Activity(new FakeGh(), _ => new OriginLookup(remote), null, (alias, _) => Task.FromResult<string?>(alias));
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);
        activity.ForFolder(@"c:\r\tool").ShouldNotBeNull();
        remote = "git@gitlab.com:acme/tool.git";

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.ForFolder(@"c:\r\tool").ShouldBeNull();
        activity.Knows(@"c:\r\tool").ShouldBeTrue();
    }

    [Fact]
    public async Task An_echo_from_ssh_keeps_the_pills_of_a_dash_name_host()
    {
        var activity = Activity(new FakeGh(), _ => new OriginLookup("git@github.com-corp:acme/tool.git"), null, (alias, _) => Task.FromResult<string?>(alias));

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.ForFolder(@"c:\r\tool").ShouldNotBeNull();
    }

    [Fact]
    public async Task A_remote_that_names_GitHub_asks_nobody()
    {
        var gh = new FakeGh();
        var activity = Activity(gh, _ => new OriginLookup("git@github.com:acme/tool.git"), null, (_, _) => throw new InvalidOperationException("must not be asked"));

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.ForFolder(@"c:\r\tool").ShouldNotBeNull();
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
        await activity.RefreshAsync([@"c:	ool"], CancellationToken.None);

        activity.Replace(repo, GitHubItemKind.Issue, ListOf(7), started, out _).ShouldBeFalse();

        activity.ForFolder(@"c:	ool")!.OpenIssues.ShouldBe(3);
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
