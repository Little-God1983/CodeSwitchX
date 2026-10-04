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

    /// <summary>A workspace's number is what the user says and presses for it ("chat 3"): the lowest free one from 1, freed on removal.</summary>
    [Fact]
    public async Task Added_workspaces_take_the_lowest_free_number_and_a_removed_one_frees_its_number()
    {
        var ct = TestContext.Current.CancellationToken;
        var track = (await _store.GetTracksAsync(ct))[0];
        Workspace Named(string name) => new() { Name = name, RootPath = @"c:\repo\" + name, TrackId = track.Id };
        var (a, b, c) = (Named("a"), Named("b"), Named("c"));
        foreach (var w in new[] { a, b, c })
        {
            await _store.AddAsync(w, ct);
        }

        await _store.RemoveAsync(b.Id, ct);
        var d = Named("d");
        await _store.AddAsync(d, ct);
        var e = Named("e");
        await _store.AddAsync(e, ct);

        (a.Number, c.Number, d.Number, e.Number).ShouldBe((1, 3, 2, 4));
        (await _store.GetAllAsync(ct)).ToDictionary(w => w.Name, w => w.Number)
            .ShouldBe(new Dictionary<string, int> { ["a"] = 1, ["c"] = 3, ["d"] = 2, ["e"] = 4 }, ignoreOrder: true);
    }

    [Fact]
    public async Task A_number_given_to_an_added_workspace_is_replaced_by_the_lowest_free_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var track = (await _store.GetTracksAsync(ct))[0];
        var workspace = new Workspace { Name = "App", RootPath = @"c:\repo\app", TrackId = track.Id, Number = 7 };

        await _store.AddAsync(workspace, ct);

        (await _store.GetAllAsync(ct)).ShouldHaveSingleItem().Number.ShouldBe(1);
    }

    /// <summary>An edit saves a whole workspace object, which may come from anywhere: the number stays the store's.</summary>
    [Fact]
    public async Task An_update_never_changes_a_number()
    {
        var ct = TestContext.Current.CancellationToken;
        var track = (await _store.GetTracksAsync(ct))[0];
        var workspace = new Workspace { Name = "App", RootPath = @"c:\repo\app", TrackId = track.Id };
        await _store.AddAsync(workspace, ct);

        await _store.UpdateAsync(new Workspace { Id = workspace.Id, Name = "Renamed", RootPath = workspace.RootPath, TrackId = track.Id, Number = 5 }, ct);

        var loaded = (await _store.GetAllAsync(ct)).ShouldHaveSingleItem();
        loaded.Name.ShouldBe("Renamed");
        loaded.Number.ShouldBe(1);
    }

    [Fact]
    public async Task Replacing_the_worktrees_changes_only_the_worktree_rows()
    {
        // The git refresh holds the workspace as loaded at startup; saving that whole object would write stale scalars back.
        var track = (await _store.GetTracksAsync(TestContext.Current.CancellationToken))[0];
        var kept = new Worktree { Path = @"c:\repo\app-wt", Branch = "wt" };
        var workspace = new Workspace { Name = "App", RootPath = @"c:\repo\app", TrackId = track.Id, Worktrees = { kept, new Worktree { Path = @"c:\repo\app-old", Branch = "old" } } };
        await _store.AddAsync(workspace, TestContext.Current.CancellationToken);
        await _store.UpdateAsync(new Workspace { Id = workspace.Id, Name = "Renamed", RootPath = workspace.RootPath, TrackId = track.Id, AutoStart = true, Worktrees = workspace.Worktrees }, TestContext.Current.CancellationToken);

        await _store.ReplaceWorktreesAsync(workspace.Id,
            [new Worktree { Id = kept.Id, WorkspaceId = workspace.Id, Path = kept.Path, Branch = "wt2" }, new Worktree { WorkspaceId = workspace.Id, Path = @"c:\repo\app-hotfix", Branch = "hotfix" }],
            TestContext.Current.CancellationToken);

        var loaded = (await _store.GetAllAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        loaded.Name.ShouldBe("Renamed");
        loaded.AutoStart.ShouldBeTrue();
        loaded.Worktrees.Select(w => (w.Path, w.Branch)).ShouldBe([(@"c:\repo\app-wt", "wt2"), (@"c:\repo\app-hotfix", "hotfix")], ignoreOrder: true);
        loaded.Worktrees.Single(w => w.Path == @"c:\repo\app-wt").Id.ShouldBe(kept.Id);
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
    public async Task A_code_workspace_may_start_with_a_folder_registered_as_its_own_workspace()
    {
        // A shared repository (an installer SDK) is its own workspace and the first folder of the products that use it.
        var track = (await _store.GetTracksAsync(TestContext.Current.CancellationToken))[0];
        await _store.AddAsync(new Workspace { Name = "SDK", RootPath = @"c:\repo\sdk", TrackId = track.Id }, TestContext.Current.CancellationToken);

        await _store.AddAsync(new Workspace { Name = "Installer", RootPath = @"c:\repo\sdk", WorkspaceFile = @"c:\repo\installer.code-workspace", TrackId = track.Id }, TestContext.Current.CancellationToken);
        await _store.AddAsync(new Workspace { Name = "Suite", RootPath = @"C:\Repo\SDK", WorkspaceFile = @"c:\repo\suite.code-workspace", TrackId = track.Id }, TestContext.Current.CancellationToken);

        (await _store.GetAllAsync(TestContext.Current.CancellationToken)).Select(w => w.Name).ShouldBe(["Installer", "SDK", "Suite"]);
        (await _store.FindByTargetAsync(@"c:\repo\sdk", null, TestContext.Current.CancellationToken)).ShouldNotBeNull().Name.ShouldBe("SDK");
        (await _store.FindByTargetAsync(@"c:\repo\sdk", @"C:\Repo\Suite.code-workspace", TestContext.Current.CancellationToken)).ShouldNotBeNull().Name.ShouldBe("Suite");
        (await _store.FindByTargetAsync(@"c:\repo\sdk", @"c:\repo\other.code-workspace", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task The_same_code_workspace_file_cannot_be_registered_twice()
    {
        var track = (await _store.GetTracksAsync(TestContext.Current.CancellationToken))[0];
        await _store.AddAsync(new Workspace { Name = "A", RootPath = @"c:\repo\sdk", WorkspaceFile = @"c:\repo\installer.code-workspace", TrackId = track.Id }, TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<DuplicateWorkspaceException>(
            () => _store.AddAsync(new Workspace { Name = "B", RootPath = @"c:\repo\other", WorkspaceFile = @"C:\Repo\Installer.code-workspace", TrackId = track.Id }, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("Installer.code-workspace");
    }

    [Fact]
    public async Task The_workspace_file_is_stored_canonical()
    {
        var track = (await _store.GetTracksAsync(TestContext.Current.CancellationToken))[0];

        await _store.AddAsync(new Workspace { Name = "A", RootPath = @"c:\repo\sdk", WorkspaceFile = @"c:/repo/sub/../Installer.code-workspace", TrackId = track.Id }, TestContext.Current.CancellationToken);

        (await _store.GetAllAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem().WorkspaceFile.ShouldBe(@"c:\repo\Installer.code-workspace");
    }

    [Fact]
    public async Task Adds_of_one_target_that_race_store_it_once()
    {
        // The duplicate check reads before it inserts, and no unique index backs it up: both must happen in one transaction.
        var track = (await _store.GetTracksAsync(TestContext.Current.CancellationToken))[0];
        var adds = Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            try
            {
                await _store.AddAsync(new Workspace { Name = $"A{i}", RootPath = @"c:\repo\sdk", WorkspaceFile = @"c:\repo\installer.code-workspace", TrackId = track.Id }, TestContext.Current.CancellationToken);
                return true;
            }
            catch (DuplicateWorkspaceException)
            {
                return false;
            }
        }));

        (await Task.WhenAll(adds)).Count(added => added).ShouldBe(1);
        (await _store.GetAllAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task FindByTarget_Update_and_Remove_work()
    {
        var track = (await _store.GetTracksAsync(TestContext.Current.CancellationToken))[0];
        var workspace = new Workspace { Name = "A", RootPath = @"c:\repo\app", TrackId = track.Id };
        await _store.AddAsync(workspace, TestContext.Current.CancellationToken);

        (await _store.FindByTargetAsync(@"c:\repo\app", null, TestContext.Current.CancellationToken)).ShouldNotBeNull().Id.ShouldBe(workspace.Id);

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

    [Fact]
    public async Task Roots_that_differ_only_by_case_or_a_trailing_separator_are_the_same_workspace()
    {
        var track = (await _store.GetTracksAsync(TestContext.Current.CancellationToken))[0];
        await _store.AddAsync(new Workspace { Name = "A", RootPath = @"C:\Repo\App", TrackId = track.Id }, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<DuplicateWorkspaceException>(
            () => _store.AddAsync(new Workspace { Name = "B", RootPath = @"c:\repo\app\", TrackId = track.Id }, TestContext.Current.CancellationToken));
        (await _store.FindByTargetAsync(@"c:\repo\app", null, TestContext.Current.CancellationToken)).ShouldNotBeNull().Name.ShouldBe("A");
        (await _store.GetAllAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem().RootPath.ShouldBe(@"C:\Repo\App", "the stored path keeps its casing");
    }
}
