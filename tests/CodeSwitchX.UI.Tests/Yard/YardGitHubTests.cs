using CodeSwitchX.Core.GitHub;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Yard;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Yard;

public class YardGitHubTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "csx-yardgh-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly IWorkspaceStore _store = Substitute.For<IWorkspaceStore>();
    private readonly Track _general = new() { Name = "General", SortOrder = 0 };
    private readonly FakeGh _gh = new();

    private sealed class FakeGh : IGhCli
    {
        public int Issues { get; set; } = 2;

        public int PullRequests { get; set; }

        public bool Hang { get; set; }

        public List<string> Names { get; } = [];

        public int Reads
        {
            get
            {
                lock (Names)
                {
                    return Names.Count;
                }
            }
        }

        public Task<IReadOnlyList<string>> AccountsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["me"]);

        public async Task<string?> RunAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            lock (Names)
            {
                Names.Add(arguments.First(a => a.StartsWith("name=", StringComparison.Ordinal)));
            }

            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return "{\"data\":{\"repository\":{\"viewerPermission\":\"ADMIN\","
                + "\"issues\":{\"totalCount\":" + Issues + ",\"nodes\":[]},"
                + "\"pullRequests\":{\"totalCount\":" + PullRequests + ",\"nodes\":[]}}}}";
        }
    }

    /// <summary>
    /// Runs the first post and holds every later one until <see cref="Release"/>: the first is the round's read of the tiles,
    /// the later ones are the tiles' new lines, which the real UI thread runs some time after the background round posts them.
    /// </summary>
    private sealed class HoldingDispatcher : IUiDispatcher
    {
        private readonly List<Action> _held = [];
        private int _posts;

        public void Post(Action action)
        {
            lock (_held)
            {
                if (_posts++ > 0)
                {
                    _held.Add(action);
                    return;
                }
            }

            action();
        }

        public void Post<T>(Action<T> action, T state) => Post(() => action(state));

        public void Release()
        {
            lock (_held)
            {
                foreach (var action in _held)
                {
                    action();
                }

                _held.Clear();
            }
        }
    }

    public YardGitHubTests()
    {
        _store.GetTracksAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Track>>([_general]));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    /// <summary>A folder with a .git/HEAD on main, which is all GitInspector reads from disk.</summary>
    private string Repo(string name)
    {
        var root = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/main\n");
        return root;
    }

    private Workspace Add(string name, string root, string? file = null)
    {
        var workspace = new Workspace { Name = name, RootPath = root, WorkspaceFile = file, TrackId = _general.Id };
        return workspace;
    }

    private YardViewModel Yard(bool withGitHub, params Workspace[] workspaces) => Yard(withGitHub, new ImmediateDispatcher(), workspaces);

    private YardViewModel Yard(bool withGitHub, IUiDispatcher ui, params Workspace[] workspaces)
    {
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>(workspaces));
        var resolver = new WorkspaceResolver();
        var engine = new SessionEngine(_bus, resolver, _time, NullLogger<SessionEngine>.Instance);
        var pricing = Substitute.For<IPricingProvider>();
        pricing.Pricing.Returns(PricingTable.Default);
        var registry = new WorkspaceRegistry(_store, resolver, _bus);
        // Git answers "clean" to status and each folder's own name as its origin: github.com/acme/<folder name>.
        var git = new GitInspector((dir, args, _) => Task.FromResult<string?>(string.Empty));
        var activity = withGitHub
            ? new GitHubActivity(new GitHubReader(_gh), (path, _) => Task.FromResult<string?>($"https://github.com/acme/{Path.GetFileName(path)}.git"),
                NullLogger<GitHubActivity>.Instance)
            : null;
        return new YardViewModel(_store, registry, engine, pricing, git, _bus, ui, _time, NullLogger<YardViewModel>.Instance, gitHub: activity);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(until, "the condition never came true");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Waits until the reads stop coming in, so a later count is not a race with the rounds still running.</summary>
    private async Task Quiet()
    {
        var last = -1;
        while (_gh.Reads != last)
        {
            last = _gh.Reads;
            await Task.Delay(150, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_line_gets_pills_only_for_counts_above_zero()
    {
        var app = Add("App", Repo("app"));
        var yard = Yard(true, app);
        _gh.Issues = 2;
        _gh.PullRequests = 0;
        await yard.InitializeAsync(CancellationToken.None);

        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Issues is not null);

        var line = yard.FindTile(app.Id)!.GitLines[0];
        line.Issues!.Count.ShouldBe(2);
        line.PullRequests.ShouldBeNull();
    }

    [Fact]
    public async Task Each_repository_line_gets_its_own_pills()
    {
        var sdk = Repo("sdk");
        var nexus = Repo("nexus");
        var file = Path.Combine(_tempRoot, "Full.code-workspace");
        File.WriteAllText(file, """{ "folders": [ { "path": "sdk" }, { "path": "nexus" } ] }""");
        var full = Add("Full", sdk, file);
        var yard = Yard(true, full);
        await yard.InitializeAsync(CancellationToken.None);

        await Eventually(() => yard.FindTile(full.Id)!.GitLines is [{ Issues: not null }, { Issues: not null }]);

        var lines = yard.FindTile(full.Id)!.GitLines;
        lines[0].Issues!.Activity.Repo.ShouldBe(new GitHubRepo("acme", "sdk"));
        lines[1].Issues!.Activity.Repo.ShouldBe(new GitHubRepo("acme", "nexus"));
    }

    [Fact]
    public async Task The_first_git_round_starts_the_first_github_round()
    {
        var app = Add("App", Repo("app"));
        var yard = Yard(true, app);
        _gh.Hang = true;
        await yard.InitializeAsync(CancellationToken.None);

        await Eventually(() => _gh.Reads >= 1);

        _gh.Names.ShouldContain("name=app");
    }

    [Fact]
    public async Task The_first_github_round_does_not_wait_for_the_tiles_to_show_their_lines()
    {
        var app = Add("App", Repo("app"));
        var ui = new HoldingDispatcher();
        var yard = Yard(true, ui, app);

        await yard.InitializeAsync(CancellationToken.None);

        await Eventually(() => _gh.Reads >= 1);
        ui.Release();
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Issues is not null);
    }

    [Fact]
    public async Task A_folder_seen_later_is_read_without_waiting_for_the_timer()
    {
        var app = Add("App", Repo("app"));
        var yard = Yard(true, app);
        await yard.InitializeAsync(CancellationToken.None);
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Issues is not null);
        var later = Add("Later", Repo("later"));

        _bus.Publish(new WorkspaceRegistered(later));

        await Eventually(() => yard.FindTile(later.Id)?.GitLines[0].Issues is not null);
    }

    [Fact]
    public async Task Every_ten_minutes_all_folders_are_read_again()
    {
        var app = Add("App", Repo("app"));
        var yard = Yard(true, app);
        await yard.InitializeAsync(CancellationToken.None);
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Issues is not null);
        await Quiet();
        var before = _gh.Reads;

        _time.Advance(YardViewModel.GitHubRefreshInterval - TimeSpan.FromSeconds(1));
        await Quiet();
        _gh.Reads.ShouldBe(before, "not before the ten minutes are up");
        _time.Advance(TimeSpan.FromSeconds(1));

        await Eventually(() => _gh.Reads > before);
        _gh.Issues = 5;
        _time.Advance(YardViewModel.GitHubRefreshInterval);
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Issues!.Count == 5);
    }

    [Fact]
    public async Task A_github_round_that_never_ends_does_not_hold_up_git()
    {
        var app = Add("App", Repo("app"));
        var yard = Yard(true, app);
        _gh.Hang = true;
        await yard.InitializeAsync(CancellationToken.None);
        await Eventually(() => _gh.Reads >= 1);

        await yard.RefreshGitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var line = yard.FindTile(app.Id)!.GitLines.ShouldHaveSingleItem();
        line.Branch.ShouldBe("main");
        line.Issues.ShouldBeNull();
    }

    [Fact]
    public async Task A_round_with_nothing_new_keeps_the_lines()
    {
        var app = Add("App", Repo("app"));
        var yard = Yard(true, app);
        await yard.InitializeAsync(CancellationToken.None);
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Issues is not null);
        await Quiet();
        var lines = yard.FindTile(app.Id)!.GitLines;
        var before = _gh.Reads;

        _time.Advance(YardViewModel.GitHubRefreshInterval);
        await Eventually(() => _gh.Reads > before);
        await Quiet();
        await yard.RefreshGitAsync(CancellationToken.None);

        yard.FindTile(app.Id)!.GitLines.ShouldBeSameAs(lines, "an open popup would close if the lines were swapped for equal ones");
    }

    [Fact]
    public async Task No_activity_service_means_no_pills()
    {
        var app = Add("App", Repo("app"));
        var yard = Yard(false, app);
        await yard.InitializeAsync(CancellationToken.None);

        await yard.RefreshGitAsync(CancellationToken.None);

        var line = yard.FindTile(app.Id)!.GitLines.ShouldHaveSingleItem();
        line.Issues.ShouldBeNull();
        line.PullRequests.ShouldBeNull();
        _gh.Reads.ShouldBe(0);
    }

    private static GitHubPill Pill(int count, GitHubItemKind kind = GitHubItemKind.Issue, IReadOnlyList<GitHubItem>? items = null) =>
        new(new RepoActivity(new GitHubRepo("acme", "SomeRepo"), "me", count, count, items ?? [], items ?? []), kind);

    [Fact]
    public void The_heading_names_the_repository_and_the_count()
    {
        Pill(19).Heading.ShouldBe("SomeRepo 19 open");
    }

    [Theory]
    [InlineData(19, "+14 more. Click the pill for all of them")]
    [InlineData(5, null)]
    public void More_text_counts_what_the_preview_leaves_out(int count, string? expected)
    {
        Pill(count).MoreText.ShouldBe(expected);
    }

    [Theory]
    [InlineData("someone", "me", "by someone")]
    [InlineData("Me", "me", null)]
    [InlineData(null, "me", null)]
    public void The_author_is_named_only_when_it_is_not_the_account(string? author, string account, string? expected)
    {
        var item = new GitHubItem(7, "t", "https://github.com/acme/SomeRepo/pull/7", DateTimeOffset.Now, [], "branch", author);

        var row = GitHubRow.From(item, account, DateTimeOffset.Now);

        row.ByText.ShouldBe(expected);
        row.NumberText.ShouldBe("#7");
    }

    [Theory]
    [InlineData(3 * 60, "updated today")]
    [InlineData(4 * 24 * 60, "updated 4d ago")]
    [InlineData(157 * 24 * 60, "updated 5 months ago")]
    public void The_age_is_told_roughly(int minutesAgo, string expected)
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var item = new GitHubItem(7, "t", "https://github.com/acme/SomeRepo/issues/7", now.AddMinutes(-minutesAgo), []);

        GitHubRow.From(item, "me", now).AgeText.ShouldBe(expected);
    }
}
