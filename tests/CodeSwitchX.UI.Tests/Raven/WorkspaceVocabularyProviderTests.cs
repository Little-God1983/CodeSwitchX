using NSubstitute;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Raven;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.UI.Tests.Raven;

public sealed class WorkspaceVocabularyProviderTests
{
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly FakeTimeProvider _time = new();

    private static IWorkspaceStore StoreOf(params Workspace[] workspaces)
    {
        var store = Substitute.For<IWorkspaceStore>();
        store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(workspaces);
        return store;
    }

    private WorkspaceVocabularyProvider NewProvider(IWorkspaceStore store, Func<string, IReadOnlyList<WorkspaceFolder>?> foldersOf) =>
        new(store, foldersOf, _bus, _time);

    [Fact]
    public async Task Workspace_names_and_the_folders_of_a_code_workspace_become_words()
    {
        var store = StoreOf(
            new Workspace { Name = "ContentAutomatorX", RootPath = @"c:\a" },
            new Workspace { Name = "Diffusion-Full", RootPath = @"c:\b", WorkspaceFile = @"c:\b\full.code-workspace" });
        using var provider = NewProvider(store, _ =>
            [new WorkspaceFolder(@"c:\x\DiffusionNexus.Installer.SDK", null), new WorkspaceFolder(@"c:\x\d", "DiffusionNexus")]);

        var vocabulary = await provider.GetAsync(TestContext.Current.CancellationToken);

        vocabulary.Words.ShouldBe(["ContentAutomatorX", "Diffusion-Full", "DiffusionNexus.Installer.SDK", "DiffusionNexus"]);
        vocabulary.Corrections.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unreadable_workspace_file_leaves_only_the_name()
    {
        var store = StoreOf(new Workspace { Name = "Diffusion-Full", RootPath = @"c:\b", WorkspaceFile = @"c:\b\full.code-workspace" });
        using var provider = NewProvider(store, _ => null);

        var vocabulary = await provider.GetAsync(TestContext.Current.CancellationToken);

        vocabulary.Words.ShouldBe(["Diffusion-Full"]);
    }

    [Fact]
    public async Task Words_are_distinct_ignoring_case()
    {
        var store = StoreOf(
            new Workspace { Name = "Raven", RootPath = @"c:\a" },
            new Workspace { Name = "raven", RootPath = @"c:\b" });
        using var provider = NewProvider(store, _ => null);

        (await provider.GetAsync(TestContext.Current.CancellationToken)).Words.ShouldBe(["Raven"]);
    }

    // Every press of the mic asks for the vocabulary: it is read from the store and the workspace files once, not per press.
    [Fact]
    public async Task Two_presses_read_the_store_and_the_workspace_files_once()
    {
        var store = StoreOf(new Workspace { Name = "Diffusion-Full", RootPath = @"c:\b", WorkspaceFile = @"c:\b\full.code-workspace" });
        var fileReads = 0;
        using var provider = NewProvider(store, _ =>
        {
            Interlocked.Increment(ref fileReads);
            return [new WorkspaceFolder(@"c:\x\d", "DiffusionNexus")];
        });

        var first = await provider.GetAsync(TestContext.Current.CancellationToken);
        var second = await provider.GetAsync(TestContext.Current.CancellationToken);

        second.Words.ShouldBe(first.Words);
        await store.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
        fileReads.ShouldBe(1);
    }

    public static TheoryData<string> WorkspaceSetChanges => ["registered", "unregistered", "roots changed"];

    [Theory]
    [MemberData(nameof(WorkspaceSetChanges))]
    public async Task A_change_to_the_workspaces_reads_them_again(string change)
    {
        var store = StoreOf(new Workspace { Name = "Raven", RootPath = @"c:\a" });
        using var provider = NewProvider(store, _ => null);
        await provider.GetAsync(TestContext.Current.CancellationToken);
        var shop = new Workspace { Name = "Shop", RootPath = @"c:\shop" };
        store.GetAllAsync(Arg.Any<CancellationToken>()).Returns([new Workspace { Name = "Raven", RootPath = @"c:\a" }, shop]);

        switch (change)
        {
            case "registered":
                _bus.Publish(new WorkspaceRegistered(shop));
                break;
            case "unregistered":
                _bus.Publish(new WorkspaceUnregistered(Guid.NewGuid()));
                break;
            default:
                _bus.Publish(new WorkspaceRootsChanged());
                break;
        }

        var vocabulary = await provider.GetAsync(TestContext.Current.CancellationToken);

        vocabulary.Words.ShouldBe(["Raven", "Shop"]);
        await store.Received(2).GetAllAsync(Arg.Any<CancellationToken>());
    }

    // A .code-workspace file whose folder list was edited announces nothing: the vocabulary is read again after ten minutes.
    [Fact]
    public async Task The_vocabulary_is_read_again_after_ten_minutes()
    {
        var store = StoreOf(new Workspace { Name = "Raven", RootPath = @"c:\a" });
        using var provider = NewProvider(store, _ => null);
        await provider.GetAsync(TestContext.Current.CancellationToken);

        _time.Advance(WorkspaceVocabularyProvider.MaxAge - TimeSpan.FromSeconds(1));
        await provider.GetAsync(TestContext.Current.CancellationToken);
        await store.Received(1).GetAllAsync(Arg.Any<CancellationToken>());

        _time.Advance(TimeSpan.FromSeconds(1));
        await provider.GetAsync(TestContext.Current.CancellationToken);

        await store.Received(2).GetAllAsync(Arg.Any<CancellationToken>());
        WorkspaceVocabularyProvider.MaxAge.ShouldBe(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task A_read_that_failed_is_not_kept()
    {
        var store = Substitute.For<IWorkspaceStore>();
        store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromException<IReadOnlyList<Workspace>>(new IOException("database locked")),
            Task.FromResult<IReadOnlyList<Workspace>>([new Workspace { Name = "Raven", RootPath = @"c:\a" }]));
        using var provider = NewProvider(store, _ => null);

        await Should.ThrowAsync<IOException>(() => provider.GetAsync(TestContext.Current.CancellationToken));
        var vocabulary = await provider.GetAsync(TestContext.Current.CancellationToken);

        vocabulary.Words.ShouldBe(["Raven"]);
    }

    // The workspaces changed while the vocabulary was being read: what that read found is not kept.
    [Fact]
    public async Task A_read_that_a_change_overtook_is_not_kept()
    {
        var reading = new TaskCompletionSource<IReadOnlyList<Workspace>>();
        var store = Substitute.For<IWorkspaceStore>();
        store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            reading.Task,
            Task.FromResult<IReadOnlyList<Workspace>>([new Workspace { Name = "Shop", RootPath = @"c:\shop" }]));
        using var provider = NewProvider(store, _ => null);

        var stale = provider.GetAsync(TestContext.Current.CancellationToken);
        _bus.Publish(new WorkspaceRootsChanged());
        reading.SetResult([new Workspace { Name = "Raven", RootPath = @"c:\a" }]);
        await stale;

        (await provider.GetAsync(TestContext.Current.CancellationToken)).Words.ShouldBe(["Shop"]);
    }
}
