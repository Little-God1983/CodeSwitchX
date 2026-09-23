using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CodeSwitchX.Core.Tests.Workspaces;

public class WorkspaceRegistryTests
{
    private readonly IWorkspaceStore _store = Substitute.For<IWorkspaceStore>();
    private readonly WorkspaceResolver _resolver = new();
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly List<object> _messages = [];
    private readonly WorkspaceRegistry _registry;

    public WorkspaceRegistryTests()
    {
        _bus.Subscribe<WorkspaceRegistered>(_messages.Add);
        _bus.Subscribe<WorkspaceUnregistered>(_messages.Add);
        _bus.Subscribe<WorkspaceRootsChanged>(_messages.Add);
        _registry = new WorkspaceRegistry(_store, _resolver, _bus);
    }

    [Fact]
    public async Task Register_normalises_paths_saves_and_reloads_the_resolver()
    {
        var workspace = new Workspace { Name = "App", RootPath = @"C:\Repo\App\", Worktrees = { new Worktree { Path = @"C:/Repo/App-wt" } } };
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([workspace]));

        await _registry.RegisterAsync(workspace, CancellationToken.None);

        workspace.RootPath.ShouldBe(@"c:\repo\app");
        workspace.Worktrees[0].Path.ShouldBe(@"c:\repo\app-wt");
        workspace.Worktrees[0].WorkspaceId.ShouldBe(workspace.Id);
        await _store.Received(1).AddAsync(workspace, Arg.Any<CancellationToken>());
        _resolver.Resolve(@"C:\Repo\App\src").ShouldBe(workspace.Id);
        _resolver.Resolve(@"C:\Repo\App-wt\x").ShouldBe(workspace.Id);
        _messages.OfType<WorkspaceRootsChanged>().ShouldNotBeEmpty();
        _messages.OfType<WorkspaceRegistered>().ShouldHaveSingleItem().Workspace.ShouldBeSameAs(workspace);
    }

    [Fact]
    public async Task Unregister_removes_and_reloads()
    {
        var id = Guid.NewGuid();
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([]));

        await _registry.UnregisterAsync(id, CancellationToken.None);

        await _store.Received(1).RemoveAsync(id, Arg.Any<CancellationToken>());
        _messages.OfType<WorkspaceUnregistered>().ShouldHaveSingleItem().WorkspaceId.ShouldBe(id);
    }
}
