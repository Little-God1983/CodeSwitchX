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
    private readonly List<YardViewModel> _yards = [];
    private GitHubActivity? _activity;
    private string? _profile;
    private Func<string, string>? _gitRunner;

    /// <summary>What each folder's git says its origin is: github.com/acme/&lt;folder name&gt;.</summary>
    private Func<string, OriginLookup> _originOf = path => new OriginLookup($"https://github.com/acme/{Path.GetFileName(path)}.git");

    private sealed class FakeGh : IGhCli
    {
        public int Issues { get; set; } = 2;

        public int PullRequests { get; set; }

        public bool Hang { get; set; }

        /// <summary>When set, every read waits for it.</summary>
        public TaskCompletionSource? Gate { get; set; }

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

        public async Task<GhAnswer> AnswerAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct) =>
            new(await RunAsAsync(login, arguments, ct));

        public async Task<GhAnswer> ListAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct) =>
            new(await RunAsAsync(login, arguments, ct));

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

            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(ct);
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

    /// <summary>Runs every post at once but one: the Nth after <see cref="BlockNth"/> waits, on the posting thread, until <see cref="Release"/>, as a post that lands on the UI thread late.</summary>
    private sealed class LateDispatcher : IUiDispatcher
    {
        private readonly ManualResetEventSlim _release = new();
        private int _blockAt = -1;
        private int _posts;

        public bool Blocked { get; private set; }

        public void BlockNth(int n)
        {
            _release.Reset();
            _posts = 0;
            Blocked = false;
            Volatile.Write(ref _blockAt, n);
        }

        public void Release() => _release.Set();

        public void Post(Action action)
        {
            if (Volatile.Read(ref _blockAt) > 0 && Interlocked.Increment(ref _posts) == _blockAt)
            {
                Blocked = true;
                _release.Wait(TimeSpan.FromSeconds(30));
            }

            action();
        }

        public void Post<T>(Action<T> action, T state) => Post(() => action(state));
    }

    public YardGitHubTests()
    {
        _store.GetTracksAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Track>>([_general]));
    }

    public void Dispose()
    {
        foreach (var yard in _yards)
        {
            yard.Dispose();
        }

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
        // Git answers "clean" to status.
        var git = new GitInspector((dir, args, _) => Task.FromResult<string?>(_gitRunner?.Invoke(args) ?? string.Empty), _profile);
        _activity = withGitHub
            ? new GitHubActivity(_gh, new GitHubReader(_gh), (path, _) => Task.FromResult(_originOf(path)), NullLogger<GitHubActivity>.Instance)
            : null;
        var yard = new YardViewModel(_store, registry, engine, pricing, git, _bus, ui, _time, NullLogger<YardViewModel>.Instance, gitHub: _activity);
        _yards.Add(yard);
        return yard;
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
    public async Task A_folder_that_is_no_repository_gets_no_origin_lookup_and_no_pills()
    {
        // A git above the user profile would answer for it; GitInspector stops there, so the tile reads "no git".
        var plain = Path.Combine(_tempRoot, "plain");
        Directory.CreateDirectory(plain);
        var asked = new List<string>();
        _originOf = path =>
        {
            lock (asked)
            {
                asked.Add(path);
            }

            return new OriginLookup("https://github.com/acme/other.git");
        };
        var app = Add("Plain", plain);
        var yard = Yard(true, app);

        await yard.InitializeAsync(CancellationToken.None);
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Text == "no git");
        await Task.Delay(300, TestContext.Current.CancellationToken);

        lock (asked)
        {
            asked.ShouldBeEmpty();
        }

        yard.FindTile(app.Id)!.GitLines[0].Issues.ShouldBeNull();
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
        var lib = Add("Lib", Repo("lib"));
        var yard = Yard(true, app, lib);
        await yard.InitializeAsync(CancellationToken.None);
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Issues is not null && yard.FindTile(lib.Id)!.GitLines[0].Issues is not null);
        await Quiet();
        var before = _gh.Reads;

        _time.Advance(YardViewModel.GitHubRefreshInterval - TimeSpan.FromSeconds(1));
        await Quiet();
        _gh.Reads.ShouldBe(before, "not before the ten minutes are up");
        _time.Advance(TimeSpan.FromSeconds(1));

        await Eventually(() => _gh.Reads >= before + 2);
        await Quiet();
        _gh.Names.Count(n => n == "name=app").ShouldBeGreaterThan(1, "the first folder is read again");
        _gh.Names.Count(n => n == "name=lib").ShouldBeGreaterThan(1, "the second folder is read again");
        _gh.Issues = 5;
        _time.Advance(YardViewModel.GitHubRefreshInterval);
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Issues!.Count == 5 && yard.FindTile(lib.Id)!.GitLines[0].Issues!.Count == 5);
    }

    [Fact]
    public async Task A_workspace_added_again_shows_no_old_counts()
    {
        var app = Add("App", Repo("app"));
        var yard = Yard(true, app);
        await yard.InitializeAsync(CancellationToken.None);
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Issues is not null);
        _bus.Publish(new WorkspaceUnregistered(app.Id));
        yard.FindTile(app.Id).ShouldBeNull();
        _gh.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _bus.Publish(new WorkspaceRegistered(app));

        await Eventually(() => yard.FindTile(app.Id)?.GitLines[0].Branch == "main");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        yard.FindTile(app.Id)!.GitLines[0].Issues.ShouldBeNull("the counts of the removed tile are old; the new read has not come back");
        _gh.Gate.SetResult();
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Issues is not null);
    }

    [Fact]
    public async Task A_workspace_added_again_is_read_fresh_when_a_round_wrote_back_after_it_was_removed()
    {
        var app = Add("App", Repo("app"));
        var yard = Yard(true, app);
        using var release = new ManualResetEventSlim();
        var lookups = 0;
        var origin = _originOf;
        _originOf = path =>
        {
            Interlocked.Increment(ref lookups);
            release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            return origin(path);
        };
        await yard.InitializeAsync(CancellationToken.None);
        await Eventually(() => Volatile.Read(ref lookups) >= 1);
        _bus.Publish(new WorkspaceUnregistered(app.Id));
        await yard.RefreshGitAsync(CancellationToken.None);
        release.Set();
        await Eventually(() => _activity!.Knows(app.RootPath)); // the round that was in flight wrote its mapping back
        await yard.RefreshGitAsync(CancellationToken.None); // the next pass sweeps it
        _activity!.Knows(app.RootPath).ShouldBeFalse();
        _gh.Issues = 7;

        _bus.Publish(new WorkspaceRegistered(app));

        var until = DateTime.UtcNow.AddSeconds(5);
        while (yard.FindTile(app.Id)?.GitLines[0].Issues?.Count != 7 && DateTime.UtcNow < until)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        yard.FindTile(app.Id)!.GitLines[0].Issues!.Count.ShouldBe(7);
    }

    [Fact]
    public async Task A_workspace_removed_and_added_again_during_a_held_round_is_asked_for_again()
    {
        var app = Add("App", Repo("app"));
        var yard = Yard(true, app);
        using var release = new ManualResetEventSlim();
        var lookups = 0;
        var origin = _originOf;
        _originOf = path =>
        {
            Interlocked.Increment(ref lookups);
            release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            return origin(path);
        };
        await yard.InitializeAsync(CancellationToken.None);
        await Eventually(() => Volatile.Read(ref lookups) >= 1);
        _bus.Publish(new WorkspaceUnregistered(app.Id));
        _bus.Publish(new WorkspaceRegistered(app));
        await Eventually(() => yard.FindTile(app.Id)?.GitLines[0].Branch == "main");
        await yard.RefreshGitAsync(CancellationToken.None);

        release.Set();

        await Eventually(() => Volatile.Read(ref lookups) >= 2);
    }

    [Fact]
    public async Task A_folder_that_left_the_workspace_file_is_dropped_by_the_next_git_round()
    {
        var sdk = Repo("sdk");
        var nexus = Repo("nexus");
        var file = Path.Combine(_tempRoot, "Full.code-workspace");
        File.WriteAllText(file, """{ "folders": [ { "path": "sdk" }, { "path": "nexus" } ] }""");
        var full = Add("Full", sdk, file);
        var yard = Yard(true, full);
        await yard.InitializeAsync(CancellationToken.None);
        await Eventually(() => yard.FindTile(full.Id)!.GitLines is [{ Issues: not null }, { Issues: not null }]);
        File.WriteAllText(file, """{ "folders": [ { "path": "sdk" } ] }""");

        await yard.RefreshGitAsync(CancellationToken.None);
        await yard.RefreshGitAsync(CancellationToken.None); // the line left the tile with the first; the folder goes with the second

        _activity!.Knows(nexus).ShouldBeFalse();
        _activity.Knows(sdk).ShouldBeTrue();
    }

    [Fact]
    public async Task A_folder_that_stops_resolving_for_a_pass_keeps_its_result_and_its_pills()
    {
        var sdk = Repo("sdk");
        var nexus = Repo("nexus");
        var file = Path.Combine(_tempRoot, "Full.code-workspace");
        File.WriteAllText(file, """{ "folders": [ { "path": "sdk" }, { "path": "nexus" } ] }""");
        var full = Add("Full", sdk, file);
        var yard = Yard(true, full);
        await yard.InitializeAsync(CancellationToken.None);
        await Eventually(() => yard.FindTile(full.Id)!.GitLines is [{ Issues: not null }, { Issues: not null }]);
        await Quiet();
        var reads = _gh.Reads;
        Directory.Delete(nexus, recursive: true); // an unplugged share, say

        await yard.RefreshGitAsync(CancellationToken.None);
        Repo("nexus");
        await yard.RefreshGitAsync(CancellationToken.None);
        await Quiet();

        yard.FindTile(full.Id)!.GitLines.Count.ShouldBe(2);
        yard.FindTile(full.Id)!.GitLines[1].Issues.ShouldNotBeNull();
        _gh.Reads.ShouldBe(reads, "the result was kept, so nothing is asked again");
    }

    [Fact]
    public async Task A_git_pass_that_throws_after_computing_the_lines_still_asks_about_the_folder()
    {
        var root = Repo("app");
        Directory.CreateDirectory(Path.Combine(root, ".git", "worktrees", "wt")); // a linked worktree: the pass lists them
        _gitRunner = args => args.StartsWith("worktree", StringComparison.Ordinal) ? throw new InvalidOperationException("boom") : string.Empty;
        var app = Add("App", root);
        var yard = Yard(true, app);

        await yard.InitializeAsync(CancellationToken.None);

        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Issues is not null);
    }

    [Fact]
    public async Task Git_rounds_during_a_slow_origin_read_make_one_read()
    {
        var app = Add("App", Repo("app"));
        var yard = Yard(true, app);
        using var release = new ManualResetEventSlim();
        var lookups = 0;
        var origin = _originOf;
        _originOf = path =>
        {
            Interlocked.Increment(ref lookups);
            release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            return origin(path);
        };
        await yard.InitializeAsync(CancellationToken.None);
        await Eventually(() => Volatile.Read(ref lookups) >= 1);

        for (var i = 0; i < 3; i++)
        {
            await yard.RefreshGitAsync(CancellationToken.None);
        }

        release.Set();
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Issues is not null);
        await Quiet();
        _gh.Reads.ShouldBe(1);
        Volatile.Read(ref lookups).ShouldBe(1);
    }

    [Fact]
    public async Task A_round_that_ended_in_failure_is_asked_for_again_by_the_next_git_round()
    {
        var app = Add("App", Repo("app"));
        var yard = Yard(true, app);
        var lookups = 0;
        var origin = _originOf;
        _originOf = path => Interlocked.Increment(ref lookups) == 1 ? OriginLookup.Failure : origin(path);

        await yard.InitializeAsync(CancellationToken.None);
        await Eventually(() => Volatile.Read(ref lookups) >= 1);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (yard.FindTile(app.Id)!.GitLines[0].Issues is null && DateTime.UtcNow < until)
        {
            await yard.RefreshGitAsync(CancellationToken.None);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        yard.FindTile(app.Id)!.GitLines[0].Issues.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_change_that_lands_between_computing_the_lines_and_posting_them_still_shows_the_newer_pills()
    {
        var app = Add("App", Repo("app"));
        var late = new LateDispatcher();
        var yard = Yard(true, late, app);
        await yard.InitializeAsync(CancellationToken.None);
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Issues is not null);
        await Quiet();
        late.BlockNth(2); // the pass reads the tiles, then posts its lines
        var pass = yard.RefreshGitAsync(CancellationToken.None);
        await Eventually(() => late.Blocked);
        _gh.Issues = 5;

        await _activity!.RefreshAsync([app.RootPath], CancellationToken.None);
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Issues!.Count == 5);
        late.Release();
        await pass;

        yard.FindTile(app.Id)!.GitLines[0].Issues!.Count.ShouldBe(5);
    }

    [Fact]
    public async Task A_repository_whose_branch_cannot_be_read_keeps_its_pills()
    {
        // A reftable repository: HEAD holds a placeholder and only git can tell the branch, which it does not for a moment.
        var root = Path.Combine(_tempRoot, "headless");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/.invalid\n");
        var app = Add("Headless", root);
        var yard = Yard(true, app);

        await yard.InitializeAsync(CancellationToken.None);

        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Issues is not null);
        yard.FindTile(app.Id)!.GitLines[0].Branch.ShouldBeNull();
    }

    [Fact]
    public async Task A_folder_under_a_repository_at_the_user_profile_gets_no_origin_lookup_and_no_pills()
    {
        var home = Path.Combine(_tempRoot, "home");
        Directory.CreateDirectory(Path.Combine(home, ".git"));
        File.WriteAllText(Path.Combine(home, ".git", "HEAD"), "ref: refs/heads/main\n");
        var work = Path.Combine(home, "work");
        Directory.CreateDirectory(work);
        _profile = home;
        var asked = 0;
        _originOf = _ =>
        {
            Interlocked.Increment(ref asked);
            return new OriginLookup("https://github.com/acme/dotfiles.git");
        };
        var app = Add("Work", work);
        var yard = Yard(true, app);
        await yard.InitializeAsync(CancellationToken.None);

        await yard.RefreshGitAsync(CancellationToken.None);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Volatile.Read(ref asked).ShouldBe(0);
        yard.FindTile(app.Id)!.GitLines[0].Text.ShouldBe("no git");
        yard.FindTile(app.Id)!.GitLines[0].Issues.ShouldBeNull();
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
    public async Task The_ten_minute_round_runs_off_the_ui_thread_and_a_slow_origin_does_not_hold_it_up()
    {
        var app = Add("App", Repo("app"));
        var yard = Yard(true, app);
        await yard.InitializeAsync(CancellationToken.None);
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Issues is not null);
        await Quiet();

        // From here git blocks as it does on a folder on an offline share. The timer posts to the UI thread: here a thread
        // of its own, whose id no task can borrow, and the immediate dispatcher runs the post on it.
        using var release = new ManualResetEventSlim();
        var lookup = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var origin = _originOf;
        _originOf = path =>
        {
            lookup.TrySetResult(Environment.CurrentManagedThreadId);
            release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            return origin(path);
        };
        var ui = new Thread(() => _time.Advance(YardViewModel.GitHubRefreshInterval)) { IsBackground = true, Name = "test-ui" };

        try
        {
            ui.Start();
            ui.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue("a slow git held up the UI thread");
            (await lookup.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldNotBe(ui.ManagedThreadId);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task An_origin_that_is_gone_takes_the_pills_off_the_line()
    {
        var app = Add("App", Repo("app"));
        var yard = Yard(true, app);
        await yard.InitializeAsync(CancellationToken.None);
        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Issues is not null);
        _originOf = _ => OriginLookup.NoOrigin;

        _time.Advance(YardViewModel.GitHubRefreshInterval);

        await Eventually(() => yard.FindTile(app.Id)!.GitLines[0].Issues is null);
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
    [InlineData(19, 5, "+14 more. Click the pill for all of them")]
    [InlineData(5, 5, null)]
    [InlineData(12, 4, "+8 more. Click the pill for all of them")]
    [InlineData(5, 4, "+1 more. Click the pill for all of them")]
    [InlineData(4, 4, null)]
    public void More_text_counts_what_the_preview_leaves_out(int count, int shown, string? expected)
    {
        var items = Enumerable.Range(1, shown).Select(n => new GitHubItem(n, "t", "u", DateTimeOffset.UnixEpoch, [])).ToList();

        Pill(count, items: items).MoreText.ShouldBe(expected);
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
