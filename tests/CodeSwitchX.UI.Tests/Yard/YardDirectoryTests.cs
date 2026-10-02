using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Yard;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Yard;

public sealed class YardDirectoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private const string DiffusionFile = @"E:\Repos\Diffusion-Full.code-workspace";
    private readonly FakeTimeProvider _time = new(Now);
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly IWorkspaceStore _store = Substitute.For<IWorkspaceStore>();
    private readonly Track _tools = new() { Name = "Tools", SortOrder = 0 };
    private readonly Track _apps = new() { Name = "Apps", SortOrder = 1 };
    private readonly Workspace _codeSwitchX;
    private readonly Workspace _diffusion;
    private readonly Dictionary<string, SessionSnapshot> _sessions = [];
    private readonly YardViewModel _yard;

    public YardDirectoryTests()
    {
        _codeSwitchX = new Workspace { Name = "CodeSwitchX", RootPath = @"E:\Repos\CodeSwitchX", TrackId = _tools.Id };
        _diffusion = new Workspace
        {
            Name = "Diffusion-Full", RootPath = @"E:\Repos\DiffusionNexus.Installer.SDK", WorkspaceFile = DiffusionFile, TrackId = _apps.Id,
        };
        _store.GetTracksAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Track>>([_tools, _apps]));
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([_codeSwitchX, _diffusion]));
        var resolver = new WorkspaceResolver();
        var engine = new SessionEngine(_bus, resolver, _time, NullLogger<SessionEngine>.Instance);
        var pricing = Substitute.For<IPricingProvider>();
        pricing.Pricing.Returns(PricingTable.Default);
        _yard = new YardViewModel(_store, new WorkspaceRegistry(_store, resolver, _bus), engine, pricing,
            new GitInspector((_, _, _) => Task.FromResult<string?>(null)), _bus, new ImmediateDispatcher(), _time, NullLogger<YardViewModel>.Instance);
    }

    private YardDirectory Directory(IUiDispatcher? ui = null) => new(_yard, id => _sessions.GetValueOrDefault(id), ui ?? new ImmediateDispatcher(),
        file => file == DiffusionFile
            ? [new WorkspaceFolder(@"E:\Repos\DiffusionNexus.Installer.SDK", null), new WorkspaceFolder(@"E:\Repos\DiffusionNexus", "Nexus app")]
            : null);

    private void Chat(string id, Workspace workspace, SessionState state, string? title = "A chat", string? model = null)
    {
        var snapshot = new SessionSnapshot
        {
            SessionId = id,
            WorkspaceId = workspace.Id,
            Title = title,
            State = state,
            StartedAt = Now.AddMinutes(-30),
            LastEventAt = Now.AddMinutes(-5),
            StateSince = Now.AddMinutes(-5),
            Model = model,
            LastToolName = title is null ? null : "Edit",
            LastNotification = state == SessionState.Waiting ? "Claude needs your permission to use Bash" : null,
            Cwd = workspace.RootPath,
            LatestContext = title is null ? TokenUsage.Zero : new TokenUsage(50_000, 0, 0, 0, 0),
        };
        _sessions[id] = snapshot;
        _yard.Apply(snapshot);
    }

    [Fact]
    public async Task Every_tile_is_a_workspace_with_its_track_and_git_lines()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        await _yard.CurrentGitRefresh; // its "no git" lines would land over the ones shown here
        _yard.FindTile(_codeSwitchX.Id)!.ShowGit([new GitLine(null, "main", 0)]);
        _yard.FindTile(_diffusion.Id)!.ShowGit([new GitLine("DiffusionNexus.Installer.SDK", "develop", 3), new GitLine("Nexus app", "main", null)]);

        var workspaces = await Directory().WorkspacesAsync(CancellationToken.None);

        workspaces.Select(w => (w.Name, w.Track)).ShouldBe([("CodeSwitchX", "Tools"), ("Diffusion-Full", "Apps")]);
        workspaces[0].Git.ShouldBe([new YardGitLine(null, "main", "clean")]);
        workspaces[1].Git.ShouldBe([new YardGitLine("DiffusionNexus.Installer.SDK", "develop", "3 changed"), new YardGitLine("Nexus app", "main", null)]);
    }

    [Fact]
    public async Task A_workspace_file_s_folders_are_named_as_the_file_names_them()
    {
        await _yard.InitializeAsync(CancellationToken.None);

        var workspaces = await Directory().WorkspacesAsync(CancellationToken.None);

        workspaces[0].Folders.ShouldBe([new YardFolder("CodeSwitchX", @"E:\Repos\CodeSwitchX")]);
        workspaces[1].Folders.ShouldBe([
            new YardFolder("DiffusionNexus.Installer.SDK", @"E:\Repos\DiffusionNexus.Installer.SDK"),
            new YardFolder("Nexus app", @"E:\Repos\DiffusionNexus"),
        ]);
    }

    [Fact]
    public async Task The_root_comes_first_even_when_the_workspace_file_lists_only_folders_below_it()
    {
        _diffusion.RootPath = @"E:\Repos\Diffusion";
        _diffusion.WorkspaceFile = DiffusionFile;
        await _yard.InitializeAsync(CancellationToken.None);
        var directory = new YardDirectory(_yard, _ => null, new ImmediateDispatcher(),
            _ => [new WorkspaceFolder(@"E:\Repos\Diffusion\src\App", null), new WorkspaceFolder(@"E:\Repos\Diffusion\src\Installer", null)]);

        var workspaces = await directory.WorkspacesAsync(CancellationToken.None);

        workspaces[1].Folders.ShouldBe([
            new YardFolder("Diffusion", @"E:\Repos\Diffusion"),
            new YardFolder("App", @"E:\Repos\Diffusion\src\App"),
            new YardFolder("Installer", @"E:\Repos\Diffusion\src\Installer"),
        ]);
    }

    [Fact]
    public async Task A_root_the_workspace_file_lists_later_comes_first_under_the_file_s_name_for_it()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        var directory = new YardDirectory(_yard, _ => null, new ImmediateDispatcher(),
            _ => [new WorkspaceFolder(@"E:\Repos\DiffusionNexus", null), new WorkspaceFolder(@"e:\repos\diffusionnexus.installer.sdk", "Installer")]);

        var workspaces = await directory.WorkspacesAsync(CancellationToken.None);

        workspaces[1].Folders.Select(f => f.Name).ShouldBe(["Installer", "DiffusionNexus"]);
    }

    [Fact]
    public async Task A_workspace_file_that_cannot_be_read_now_leaves_the_root_folder()
    {
        _diffusion.WorkspaceFile = @"E:\Repos\locked.code-workspace";
        await _yard.InitializeAsync(CancellationToken.None);

        var workspaces = await Directory().WorkspacesAsync(CancellationToken.None);

        workspaces[1].Folders.ShouldBe([new YardFolder("DiffusionNexus.Installer.SDK", @"E:\Repos\DiffusionNexus.Installer.SDK")]);
    }

    [Fact]
    public async Task The_chats_are_the_rows_the_tiles_show_with_what_the_engine_knows_of_them()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        Chat("s-wait", _codeSwitchX, SessionState.Waiting, "Raven brain", "claude-sonnet-5-5");
        Chat("s-work", _diffusion, SessionState.Working, "Installer icons");
        _yard.Tick(Now);

        var chats = await Directory().ChatsAsync(CancellationToken.None);

        var waiting = chats.Single(c => c.Id == "s-wait");
        waiting.Title.ShouldBe("Raven brain");
        waiting.WorkspaceId.ShouldBe(_codeSwitchX.Id);
        waiting.Workspace.ShouldBe("CodeSwitchX");
        waiting.State.ShouldBe(SessionState.Waiting);
        waiting.NeedsYou.ShouldBeTrue();
        waiting.StateFor.ShouldBe("5m");
        waiting.Model.ShouldBe("claude-sonnet-5-5");
        waiting.LastTool.ShouldBe("Edit");
        waiting.LastNotification.ShouldBe("Claude needs your permission to use Bash");
        waiting.Cwd.ShouldBe(@"E:\Repos\CodeSwitchX");
        waiting.ContextFill.ShouldBeGreaterThan(0);
        chats.Single(c => c.Id == "s-work").NeedsYou.ShouldBeFalse();
    }

    [Fact]
    public async Task A_session_that_is_no_chat_is_left_out_like_on_the_board()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        Chat("s-idle", _codeSwitchX, SessionState.Idle, title: null);

        (await Directory().ChatsAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_UI_thread_that_never_answers_fails_the_read_instead_of_hanging_it()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        var directory = Directory(new NeverDispatcher());
        directory.UiTimeout = TimeSpan.FromMilliseconds(50);

        await Should.ThrowAsync<TimeoutException>(() => directory.ChatsAsync(CancellationToken.None));
    }

    private sealed class NeverDispatcher : IUiDispatcher
    {
        public void Post(Action action)
        {
        }

        public void Post<T>(Action<T> action, T state)
        {
        }
    }
}
