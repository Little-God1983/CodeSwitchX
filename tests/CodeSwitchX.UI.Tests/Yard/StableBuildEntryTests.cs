using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Telemetry;
using CodeSwitchX.Tests;
using CodeSwitchX.UI.Yard;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>#423: a tile's Build new version / Run entries.</summary>
public class StableBuildEntryTests
{
    private readonly Launcher _launcher = new();
    private readonly Dialogs _dialogs = new();
    private readonly WorkspaceTileViewModel _tile;

    public StableBuildEntryTests()
    {
        var app = new Workspace { Name = "App", RootPath = @"c:\repo\app", TrackId = Guid.NewGuid() };
        var store = Substitute.For<IWorkspaceStore>();
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var resolver = new WorkspaceResolver();
        var time = new FakeTimeProvider();
        var pricing = Substitute.For<IPricingProvider>();
        pricing.Pricing.Returns(PricingTable.Default);
        var yard = new YardViewModel(store, new WorkspaceRegistry(store, resolver, bus), new SessionEngine(bus, resolver, time, NullLogger<SessionEngine>.Instance),
            pricing, new GitInspector((_, _, _) => Task.FromResult<string?>(null)), bus, new ImmediateDispatcher(), time, NullLogger<YardViewModel>.Instance,
            stableBuilds: _launcher, dialogs: _dialogs);
        _tile = new WorkspaceTileViewModel(app, yard);
    }

    private static GitLine Line(string path, string? branch = "main", int? dirty = 0, bool scripts = true, string? folder = null) =>
        new(folder, branch, dirty, path, IsRepository: true, StableBuild: scripts ? new StableBuildRepo(path, Path.GetFileName(path) + ".lnk") : null);

    [Fact]
    public void A_tile_without_scripts_has_no_entries()
    {
        _tile.ShowGit([Line(@"c:\repo\app", scripts: false)]);

        _tile.StableBuildEntries().ShouldBeEmpty();
    }

    [Fact]
    public void One_repository_gets_the_plain_headers()
    {
        _tile.ShowGit([Line(@"c:\repo\app")]);

        var entry = _tile.StableBuildEntries().ShouldHaveSingleItem();

        entry.BuildHeader.ShouldBe("Build new version");
        entry.RunHeader.ShouldBe("Run");
    }

    [Fact]
    public void Several_repositories_carry_their_folder_names()
    {
        _tile.ShowGit([Line(@"c:\repo\app\Alpha", folder: "Alpha"), Line(@"c:\repo\app\Beta", folder: "Beta")]);

        var entries = _tile.StableBuildEntries();

        entries.Select(e => e.BuildHeader).ShouldBe(["Build Alpha", "Build Beta"]);
        entries.Select(e => e.RunHeader).ShouldBe(["Run Alpha", "Run Beta"]);
    }

    [Fact]
    public void Several_repositories_go_by_the_names_their_lines_show()
    {
        // Two checkouts in folders of one name tell apart by the labels the workspace file gives them.
        _tile.ShowGit([Line(@"c:\one\app", folder: "Client"), Line(@"c:\two\app", folder: "Server")]);

        _tile.StableBuildEntries().Select(e => e.BuildHeader).ShouldBe(["Build Client", "Build Server"]);
    }

    [Fact]
    public async Task Build_starts_the_window_off_the_calling_thread()
    {
        // The launcher looks along PATH for pwsh, and a folder on an offline share there blocks for about 20 s: never on the UI thread.
        _launcher.Gate = new ManualResetEventSlim();
        _tile.ShowGit([Line(@"c:\repo\app")]);

        var building = _tile.StableBuildEntries()[0].BuildCommand.ExecuteAsync(null);

        building.IsCompleted.ShouldBeFalse("the window is started on another thread, so the click returns at once");
        _launcher.Gate.Set();
        await building;
        _launcher.Built.ShouldBe([@"c:\repo\app"]);
    }

    [Fact]
    public void Two_lines_of_one_repository_give_one_entry()
    {
        _tile.ShowGit([Line(@"c:\repo\app", folder: "app"), Line(@"C:\Repo\App", folder: "again")]);

        _tile.StableBuildEntries().ShouldHaveSingleItem().BuildHeader.ShouldBe("Build new version");
    }

    [Fact]
    public void A_feature_branch_greys_out_build_and_says_why()
    {
        _tile.ShowGit([Line(@"c:\repo\app", branch: "feature/x")]);

        var entry = _tile.StableBuildEntries().ShouldHaveSingleItem();

        entry.CanBuild.ShouldBeFalse();
        entry.BuildBlock.ShouldBe("On feature/x - a stable build runs from main or develop");
        entry.BuildCommand.CanExecute(null).ShouldBeFalse("the command itself holds the precheck, not only the menu item");
        entry.CanRun.ShouldBeTrue("Run starts what is installed, whatever the branch");
    }

    [Fact]
    public async Task A_repository_at_a_drive_root_is_named_after_the_drive()
    {
        _launcher.Shortcuts.Add(@"programs\Root.lnk");
        _tile.ShowGit([new GitLine(null, "main", 0, @"E:\", IsRepository: true, StableBuild: new StableBuildRepo(@"E:\", "Root.lnk"))]);

        await _tile.StableBuildEntries()[0].BuildCommand.ExecuteAsync(null);

        _dialogs.Asked.ShouldHaveSingleItem().ShouldStartWith(@"Build a new version of E:\?");
    }

    [Fact]
    public void Run_is_greyed_out_without_a_shortcut()
    {
        _launcher.Shortcuts.Clear();
        _tile.ShowGit([Line(@"c:\repo\app")]);

        var entry = _tile.StableBuildEntries().ShouldHaveSingleItem();

        entry.CanRun.ShouldBeFalse();
        entry.RunBlock.ShouldBe("No stable build yet");
    }

    [Fact]
    public async Task Build_asks_first_and_starts_on_yes()
    {
        _tile.ShowGit([Line(@"c:\repo\app")]);

        await _tile.StableBuildEntries()[0].BuildCommand.ExecuteAsync(null);

        _dialogs.Asked.ShouldHaveSingleItem().ShouldStartWith("Build a new version of app?");
        _launcher.Built.ShouldBe([@"c:\repo\app"]);
    }

    [Fact]
    public async Task Build_starts_nothing_on_no()
    {
        _dialogs.Answer = false;
        _tile.ShowGit([Line(@"c:\repo\app")]);

        await _tile.StableBuildEntries()[0].BuildCommand.ExecuteAsync(null);

        _launcher.Built.ShouldBeEmpty();
    }

    [Fact]
    public async Task Build_tells_when_the_window_cannot_be_started()
    {
        _launcher.Starts = false;
        _tile.ShowGit([Line(@"c:\repo\app")]);

        await _tile.StableBuildEntries()[0].BuildCommand.ExecuteAsync(null);

        _dialogs.Told.ShouldBe(["Could not start the build of app."]);
    }

    [Fact]
    public async Task Run_opens_the_repositorys_shortcut()
    {
        _tile.ShowGit([Line(@"c:\repo\app")]);

        await _tile.StableBuildEntries()[0].RunCommand.ExecuteAsync(null);

        _launcher.Ran.ShouldBe([@"programs\app.lnk"]);
    }

    [Fact]
    public async Task Run_tells_when_the_shortcut_cannot_be_opened()
    {
        _launcher.Starts = false;
        _tile.ShowGit([Line(@"c:\repo\app")]);

        await _tile.StableBuildEntries()[0].RunCommand.ExecuteAsync(null);

        _dialogs.Told.ShouldBe(["Could not start app."]);
    }

    private sealed class Launcher : IStableBuildLauncher
    {
        public bool Starts { get; set; } = true;
        public HashSet<string> Shortcuts { get; } = new(StringComparer.OrdinalIgnoreCase) { @"programs\app.lnk", @"programs\Alpha.lnk", @"programs\Beta.lnk" };
        public List<string> Built { get; } = [];
        public List<string> Ran { get; } = [];

        public string ShortcutPath(string shortcutName) => @"programs\" + shortcutName;

        public bool ShortcutExists(string shortcutPath) => Shortcuts.Contains(shortcutPath);

        /// <summary>When set, a build waits for it (at most 2 s): a build started on the caller's thread would hold the caller that long.</summary>
        public ManualResetEventSlim? Gate { get; set; }

        public bool StartBuild(StableBuildRepo repo)
        {
            Gate?.Wait(TimeSpan.FromSeconds(2));
            Built.Add(repo.RepoPath);
            return Starts;
        }

        public bool Run(string shortcutPath)
        {
            Ran.Add(shortcutPath);
            return Starts;
        }
    }

    private sealed class Dialogs : IYardDialogs
    {
        public bool Answer { get; set; } = true;
        public List<string> Asked { get; } = [];
        public List<string> Told { get; } = [];

        public bool Confirm(string question)
        {
            Asked.Add(question);
            return Answer;
        }

        public void Tell(string message) => Told.Add(message);
    }
}
