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
