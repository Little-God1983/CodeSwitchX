using CodeSwitchX.Core.GitHub;
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

        public Task<IReadOnlyList<string>> AccountsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["me"]);

        public async Task<string?> RunAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct)
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

                return Fail ? null : Graphql(Issues);
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

    private static GitHubActivity Activity(FakeGh gh, Func<string, string?>? originOf = null) =>
        new(new GitHubReader(gh), (path, _) => Task.FromResult(originOf is null ? $"https://github.com/acme/{Path.GetFileName(path)}.git" : originOf(path)),
            NullLogger<GitHubActivity>.Instance);

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

        activity.Replace(new GitHubRepo("acme", "tool"), GitHubItemKind.Issue, Enumerable.Range(1, 7).Select(Item).ToList());

        var after = activity.ForFolder(@"c:\r\tool")!;
        after.OpenIssues.ShouldBe(7);
        after.NewestIssues.Select(i => i.Number).ShouldBe([1, 2, 3, 4, 5]);
        after.OpenPullRequests.ShouldBe(before.OpenPullRequests);
        after.NewestPullRequests.ShouldBe(before.NewestPullRequests);
        changes.ShouldBe(1);
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

    [Fact]
    public async Task A_failed_origin_lookup_keeps_the_repository_the_folder_had()
    {
        var gh = new FakeGh();
        string? origin = "https://github.com/acme/tool.git";
        var activity = Activity(gh, _ => origin);
        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);
        var first = activity.ForFolder(@"c:\r\tool");
        first.ShouldNotBeNull();
        origin = null;

        await activity.RefreshAsync([@"c:\r\tool"], CancellationToken.None);

        activity.ForFolder(@"c:\r\tool").ShouldBeSameAs(first);
    }
}
