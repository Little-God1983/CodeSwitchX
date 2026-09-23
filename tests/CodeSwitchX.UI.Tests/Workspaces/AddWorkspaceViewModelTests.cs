using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Paths;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Workspaces;

public class AddWorkspaceViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csx-add-" + Guid.NewGuid().ToString("N"), "Shop");
    private readonly IWorkspaceStore _store = Substitute.For<IWorkspaceStore>();
    private readonly Track _general = new() { Name = "General" };
    private readonly AddWorkspaceViewModel _vm;

    public AddWorkspaceViewModelTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(_root, "Shop.slnx"), string.Empty);
        _store.GetTracksAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Track>>([_general]));
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([]));
        _store.AddTrackAsync("Clients", Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(new Track { Name = "Clients", SortOrder = 1 }));
        var git = new GitInspector((_, _, _) => Task.FromResult<string?>(null));
        var registry = new WorkspaceRegistry(_store, new WorkspaceResolver(), new EventBus(NullLogger<EventBus>.Instance));
        _vm = new AddWorkspaceViewModel(new WorkspaceProbe(git), _store, registry, NullLogger<AddWorkspaceViewModel>.Instance);
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);

    [Fact]
    public async Task Probe_fills_the_form_from_the_folder()
    {
        await _vm.LoadAsync(CancellationToken.None);
        _vm.SaveCommand.CanExecute(null).ShouldBeFalse();
        _vm.InputPath = _root;

        await _vm.ProbeCommand.ExecuteAsync(null);

        _vm.IsProbed.ShouldBeTrue();
        _vm.Name.ShouldBe("Shop");
        _vm.RootPath.ShouldBe(PathNormalizer.Normalize(_root));
        _vm.IsGitRepository.ShouldBeTrue();
        _vm.Branch.ShouldBe("main");
        _vm.SolutionSummary.ShouldBe("Shop.slnx");
        _vm.SelectedTrack.ShouldBe(_general);
        _vm.ErrorMessage.ShouldBeNull();
        _vm.SaveCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task Probe_errors_are_shown_not_thrown()
    {
        await _vm.LoadAsync(CancellationToken.None);
        _vm.InputPath = Path.Combine(_root, "missing");

        await _vm.ProbeCommand.ExecuteAsync(null);

        _vm.IsProbed.ShouldBeFalse();
        _vm.ErrorMessage.ShouldNotBeNull().ShouldContain("does not exist");
    }

    [Fact]
    public async Task Save_creates_a_new_track_when_named_and_registers_the_workspace()
    {
        await _vm.LoadAsync(CancellationToken.None);
        _vm.InputPath = _root;
        await _vm.ProbeCommand.ExecuteAsync(null);
        _vm.NewTrackName = "Clients";
        _vm.SelectedAccent = "#FF8800";
        _vm.AutoStart = true;
        Workspace? saved = null;
        _vm.Saved += w => saved = w;

        await _vm.SaveCommand.ExecuteAsync(null);

        await _store.Received(1).AddTrackAsync("Clients", Arg.Any<CancellationToken>());
        await _store.Received(1).AddAsync(Arg.Is<Workspace>(w => w.Name == "Shop" && w.AccentColor == "#FF8800" && w.AutoStart && w.RootPath == PathNormalizer.Normalize(_root)), Arg.Any<CancellationToken>());
        saved.ShouldNotBeNull();
        saved.TrackId.ShouldNotBe(_general.Id);
    }

    [Fact]
    public async Task Duplicate_roots_show_an_error_and_keep_the_dialog_open()
    {
        await _vm.LoadAsync(CancellationToken.None);
        _vm.InputPath = _root;
        await _vm.ProbeCommand.ExecuteAsync(null);
        _store.AddAsync(Arg.Any<Workspace>(), Arg.Any<CancellationToken>()).Returns(_ => throw new DuplicateWorkspaceException(_vm.RootPath));
        var closed = false;
        _vm.Closed += () => closed = true;

        await _vm.SaveCommand.ExecuteAsync(null);

        _vm.ErrorMessage.ShouldNotBeNull().ShouldContain("already registered");
        closed.ShouldBeFalse();
    }
}
