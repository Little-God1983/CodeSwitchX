using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Core.Tests.Workspaces;

public class WorkspaceResolverTests
{
    private static readonly Guid App = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid App2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AppWorktree = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static WorkspaceResolver Resolver()
    {
        var resolver = new WorkspaceResolver();
        resolver.SetRoots(
        [
            new WorkspaceRoot(App, @"C:\Repo\App"),
            new WorkspaceRoot(App2, @"C:\Repo\App2"),
            new WorkspaceRoot(AppWorktree, @"C:\Repo\App\.worktrees\feature-x"),
        ]);
        return resolver;
    }

    [Fact]
    public void Sibling_roots_that_share_a_string_prefix_do_not_collide()
    {
        var resolver = Resolver();

        resolver.Resolve(@"C:\Repo\App2\src").ShouldBe(App2);
        resolver.Resolve(@"C:\Repo\App2").ShouldBe(App2);
        resolver.Resolve(@"C:\Repo\App\src").ShouldBe(App);
    }

    [Fact]
    public void Longest_matching_root_wins_so_worktrees_beat_their_parent()
    {
        Resolver().Resolve(@"C:\Repo\App\.worktrees\feature-x\src\Foo").ShouldBe(AppWorktree);
    }

    [Theory]
    [InlineData(@"c:\repo\app\SRC")]
    [InlineData(@"C:/Repo/App/src/")]
    [InlineData(@"C:\Repo\App")]
    public void Matching_is_case_and_separator_insensitive(string cwd)
    {
        Resolver().Resolve(cwd).ShouldBe(App);
    }

    [Theory]
    [InlineData(@"D:\Elsewhere")]
    [InlineData(@"C:\Repo")]
    [InlineData("")]
    [InlineData(null)]
    public void Unregistered_paths_resolve_to_null(string? cwd)
    {
        Resolver().Resolve(cwd).ShouldBeNull();
    }

    [Fact]
    public void A_workspace_registered_on_another_workspaces_worktree_owns_that_folder()
    {
        var feature = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var app = new Workspace
        {
            Id = App, Name = "App", RootPath = @"c:\code\app", TrackId = Guid.NewGuid(),
            Worktrees = { new Worktree { WorkspaceId = App, Path = @"c:\code\app-feature", Branch = "feature" } },
        };
        var appFeature = new Workspace { Id = feature, Name = "App feature", RootPath = @"c:\code\app-feature", TrackId = Guid.NewGuid() };
        var resolver = new WorkspaceResolver();

        resolver.SetRoots(WorkspaceResolver.RootsOf([app, appFeature]));

        resolver.Resolve(@"C:\code\app-feature\src").ShouldBe(feature);
    }

    [Fact]
    public void A_folder_workspace_owns_its_folder_over_a_code_workspace_that_starts_with_it()
    {
        var sdk = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var installer = Guid.Parse("66666666-6666-6666-6666-666666666666");
        // Listed by name, the code-workspace comes first; the folder must still win whatever the order.
        var workspaces = new[]
        {
            new Workspace { Id = installer, Name = "A installer", RootPath = @"c:\repo\sdk", WorkspaceFile = @"c:\repo\installer.code-workspace", TrackId = Guid.NewGuid() },
            new Workspace { Id = sdk, Name = "SDK", RootPath = @"C:\Repo\SDK", TrackId = Guid.NewGuid() },
        };
        var resolver = new WorkspaceResolver();

        resolver.SetRoots(WorkspaceResolver.RootsOf(workspaces));
        resolver.Resolve(@"C:\repo\sdk\src").ShouldBe(sdk);

        resolver.SetRoots(WorkspaceResolver.RootsOf(workspaces.Reverse()));
        resolver.Resolve(@"C:\repo\sdk\src").ShouldBe(sdk);
    }

    [Fact]
    public void Of_two_code_workspaces_that_start_with_one_folder_the_first_registered_owns_it_whatever_the_names()
    {
        var installer = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var suite = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var older = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var workspaces = new[]
        {
            new Workspace { Id = suite, Name = "A suite", RootPath = @"c:\repo\sdk", WorkspaceFile = @"c:\repo\suite.code-workspace", CreatedAt = older.AddDays(1), TrackId = Guid.NewGuid() },
            new Workspace { Id = installer, Name = "Zeta installer", RootPath = @"c:\repo\sdk", WorkspaceFile = @"c:\repo\installer.code-workspace", CreatedAt = older, TrackId = Guid.NewGuid() },
        };
        var resolver = new WorkspaceResolver();

        resolver.SetRoots(WorkspaceResolver.RootsOf(workspaces));
        resolver.Resolve(@"C:\repo\sdk\src").ShouldBe(installer);

        resolver.SetRoots(WorkspaceResolver.RootsOf(workspaces.Reverse()));
        resolver.Resolve(@"C:\repo\sdk\src").ShouldBe(installer);
    }

    [Fact]
    public void Every_folder_of_a_code_workspace_is_a_root_of_it_but_a_folder_workspace_keeps_its_own_folder()
    {
        var sdk = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var installer = Guid.Parse("66666666-6666-6666-6666-666666666666");
        const string file = @"c:\repo\installer.code-workspace";
        var workspaces = new[]
        {
            new Workspace { Id = installer, Name = "Installer", RootPath = @"c:\repo\sdk", WorkspaceFile = file, TrackId = Guid.NewGuid() },
            new Workspace { Id = sdk, Name = "SDK", RootPath = @"c:\repo\sdk", TrackId = Guid.NewGuid() },
        };
        IReadOnlyList<WorkspaceFolder>? FoldersOf(string path) =>
            path == file ? [new WorkspaceFolder(@"c:\repo\sdk", null), new WorkspaceFolder(@"c:\repo\installer", "Installer app")] : null;
        var resolver = new WorkspaceResolver();

        resolver.SetRoots(WorkspaceResolver.RootsOf(workspaces, FoldersOf));

        resolver.Resolve(@"C:\repo\sdk\src").ShouldBe(sdk);
        resolver.Resolve(@"C:\repo\installer\src").ShouldBe(installer);
    }

    [Fact]
    public void A_code_workspace_whose_file_cannot_be_read_keeps_its_root()
    {
        var workspace = new Workspace { Id = App, Name = "App", RootPath = @"c:\repo\app", WorkspaceFile = @"c:\repo\app.code-workspace", TrackId = Guid.NewGuid() };
        var resolver = new WorkspaceResolver();

        resolver.SetRoots(WorkspaceResolver.RootsOf([workspace], _ => null));

        resolver.Resolve(@"c:\repo\app\src").ShouldBe(App);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void The_target_of_a_workspace_without_a_file_is_its_folder(string? workspaceFile)
    {
        Workspace.TargetOf(@"c:\repo\app", workspaceFile).ShouldBe(@"c:\repo\app");
        new Workspace { RootPath = @"c:\repo\app", WorkspaceFile = workspaceFile }.Target.ShouldBe(@"c:\repo\app");
        Workspace.TargetOf(@"c:\repo\app", @"c:\repo\app.code-workspace").ShouldBe(@"c:\repo\app.code-workspace");
    }

    [Fact]
    public void RootsOf_includes_worktrees_as_child_roots()
    {
        var workspace = new Workspace
        {
            Id = App,
            Name = "App",
            RootPath = @"c:\repo\app",
            TrackId = Guid.NewGuid(),
            Worktrees = { new Worktree { WorkspaceId = App, Path = @"c:\repo\app-wt", Branch = "feature" } },
        };

        var roots = WorkspaceResolver.RootsOf([workspace]).ToList();

        roots.Select(r => r.Path).ShouldBe([@"c:\repo\app", @"c:\repo\app-wt"], ignoreOrder: true);
        roots.ShouldAllBe(r => r.WorkspaceId == App);
    }
}
