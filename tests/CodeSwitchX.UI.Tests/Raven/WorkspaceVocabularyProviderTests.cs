using NSubstitute;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Raven;

namespace CodeSwitchX.UI.Tests.Raven;

public sealed class WorkspaceVocabularyProviderTests
{
    private static IWorkspaceStore StoreOf(params Workspace[] workspaces)
    {
        var store = Substitute.For<IWorkspaceStore>();
        store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(workspaces);
        return store;
    }

    [Fact]
    public async Task Workspace_names_and_the_folders_of_a_code_workspace_become_words()
    {
        var store = StoreOf(
            new Workspace { Name = "ContentAutomatorX", RootPath = @"c:\a" },
            new Workspace { Name = "Diffusion-Full", RootPath = @"c:\b", WorkspaceFile = @"c:\b\full.code-workspace" });
        var provider = new WorkspaceVocabularyProvider(store, _ =>
            [new WorkspaceFolder(@"c:\x\DiffusionNexus.Installer.SDK", null), new WorkspaceFolder(@"c:\x\d", "DiffusionNexus")]);

        var vocabulary = await provider.GetAsync(CancellationToken.None);

        vocabulary.Words.ShouldBe(["ContentAutomatorX", "Diffusion-Full", "DiffusionNexus.Installer.SDK", "DiffusionNexus"]);
        vocabulary.Corrections.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unreadable_workspace_file_leaves_only_the_name()
    {
        var store = StoreOf(new Workspace { Name = "Diffusion-Full", RootPath = @"c:\b", WorkspaceFile = @"c:\b\full.code-workspace" });
        var provider = new WorkspaceVocabularyProvider(store, _ => null);

        var vocabulary = await provider.GetAsync(CancellationToken.None);

        vocabulary.Words.ShouldBe(["Diffusion-Full"]);
    }

    [Fact]
    public async Task Words_are_distinct_ignoring_case()
    {
        var store = StoreOf(
            new Workspace { Name = "Raven", RootPath = @"c:\a" },
            new Workspace { Name = "raven", RootPath = @"c:\b" });
        var provider = new WorkspaceVocabularyProvider(store, _ => null);

        (await provider.GetAsync(CancellationToken.None)).Words.ShouldBe(["Raven"]);
    }
}
