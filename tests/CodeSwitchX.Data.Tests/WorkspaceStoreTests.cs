using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Data.Tests;

public class WorkspaceStoreTests : IAsyncLifetime
{
    private readonly SqliteFixture _db = new();
    private IWorkspaceStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await _db.InitializeAsync();
        _store = _db.Get<IWorkspaceStore>();
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    [Fact]
    public async Task Initializer_seeds_a_default_track()
    {
        var tracks = await _store.GetTracksAsync(TestContext.Current.CancellationToken);

        tracks.Count.ShouldBe(1);
        tracks[0].Name.ShouldBe("General");
    }

    [Fact]
    public async Task Workspaces_round_trip_with_their_worktrees()
    {
        var track = (await _store.GetTracksAsync(TestContext.Current.CancellationToken))[0];
        var workspace = new Workspace
        {
            Name = "App",
            RootPath = @"c:\repo\app",
            WorkspaceFile = @"c:\repo\app\app.code-workspace",
            TrackId = track.Id,
            AccentColor = "#FF8800",
            HostMode = HostMode.Snap,
            AutoStart = true,
            Worktrees = { new Worktree { Path = @"c:\repo\app-wt", Branch = "feature" } },
        };

        await _store.AddAsync(workspace, TestContext.Current.CancellationToken);
        var loaded = (await _store.GetAllAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem();

        loaded.Id.ShouldBe(workspace.Id);
        loaded.Name.ShouldBe("App");
        loaded.WorkspaceFile.ShouldBe(@"c:\repo\app\app.code-workspace");
        loaded.AccentColor.ShouldBe("#FF8800");
        loaded.AutoStart.ShouldBeTrue();
        loaded.Worktrees.ShouldHaveSingleItem().Branch.ShouldBe("feature");
        loaded.CreatedAt.ShouldBe(workspace.CreatedAt);
    }

    [Fact]
    public async Task Adding_a_second_workspace_with_the_same_root_throws()
    {
        var track = (await _store.GetTracksAsync(TestContext.Current.CancellationToken))[0];
        await _store.AddAsync(new Workspace { Name = "A", RootPath = @"c:\repo\app", TrackId = track.Id }, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<DuplicateWorkspaceException>(
            () => _store.AddAsync(new Workspace { Name = "B", RootPath = @"c:\repo\app", TrackId = track.Id }));
    }

    [Fact]
    public async Task FindByRoot_Update_and_Remove_work()
    {
        var track = (await _store.GetTracksAsync(TestContext.Current.CancellationToken))[0];
        var workspace = new Workspace { Name = "A", RootPath = @"c:\repo\app", TrackId = track.Id };
        await _store.AddAsync(workspace, TestContext.Current.CancellationToken);

        (await _store.FindByRootAsync(@"c:\repo\app", TestContext.Current.CancellationToken)).ShouldNotBeNull().Id.ShouldBe(workspace.Id);

        workspace.Name = "Renamed";
        workspace.Worktrees.Add(new Worktree { Path = @"c:\repo\app-2", Branch = "b2" });
        await _store.UpdateAsync(workspace, TestContext.Current.CancellationToken);
        var updated = (await _store.GetAllAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        updated.Name.ShouldBe("Renamed");
        updated.Worktrees.Count.ShouldBe(1);

        await _store.RemoveAsync(workspace.Id, TestContext.Current.CancellationToken);
        (await _store.GetAllAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Tracks_can_be_added_and_renamed()
    {
        var track = await _store.AddTrackAsync("Client work", TestContext.Current.CancellationToken);
        track.SortOrder.ShouldBe(1);

        track.Name = "Clients";
        await _store.UpdateTrackAsync(track, TestContext.Current.CancellationToken);

        (await _store.GetTracksAsync(TestContext.Current.CancellationToken)).Select(t => t.Name).ShouldBe(["General", "Clients"]);
    }
}
