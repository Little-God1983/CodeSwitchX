using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Workspaces;

public class AddWorkspaceLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csx-launch-" + Guid.NewGuid().ToString("N"), "Shop");
    private readonly IWorkspaceStore _store = Substitute.For<IWorkspaceStore>();
    private readonly List<AddWorkspaceViewModel> _shown = [];
    private TaskCompletionSource _dialogClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AddWorkspaceLauncherTests()
    {
        Directory.CreateDirectory(_root);
        _store.GetTracksAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Track>>([new Track { Name = "General" }]));
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);

    private AddWorkspaceViewModel NewViewModel() =>
        new(new WorkspaceProbe(new GitInspector((_, _, _) => Task.FromResult<string?>(null))), _store,
            new WorkspaceRegistry(_store, new WorkspaceResolver(), new EventBus(NullLogger<EventBus>.Instance)), NullLogger<AddWorkspaceViewModel>.Instance);

    /// <summary>A launcher whose dialog stays open until <see cref="_dialogClosed"/> is set.</summary>
    private AddWorkspaceLauncher Launcher(Func<AddWorkspaceViewModel, Task>? show = null) =>
        new(NewViewModel, show ?? (vm =>
        {
            _shown.Add(vm);
            return _dialogClosed.Task;
        }), NullLogger<AddWorkspaceLauncher>.Instance);

    [Fact]
    public async Task A_second_request_while_the_dialog_is_open_does_not_open_another()
    {
        var launcher = Launcher();

        var first = launcher.OpenAsync(null);
        await launcher.OpenAsync(_root); // a drop on the Yard behind the modal dialog, or a second click while it loads

        _shown.ShouldHaveSingleItem().InputPath.ShouldBeEmpty("the second request, with its path, was not taken");
        _dialogClosed.SetResult();
        await first;
    }

    [Fact]
    public async Task A_request_after_the_dialog_closed_opens_it_again()
    {
        var launcher = Launcher();
        var first = launcher.OpenAsync(null);
        _dialogClosed.SetResult();
        await first;
        _dialogClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var second = launcher.OpenAsync(null);

        _shown.Count.ShouldBe(2);
        _dialogClosed.SetResult();
        await second;
    }

    [Fact]
    public async Task A_dialog_that_failed_to_show_does_not_keep_the_next_one_from_opening()
    {
        var shows = 0;
        var launcher = Launcher(_ => ++shows == 1 ? throw new InvalidOperationException("no owner") : Task.CompletedTask);

        await launcher.OpenAsync(null); // logged, not thrown: the Yard fires and forgets
        await launcher.OpenAsync(null);

        shows.ShouldBe(2);
    }

    [Fact]
    public async Task A_dropped_path_is_filled_in_and_detected()
    {
        var launcher = Launcher();

        var open = launcher.OpenAsync(_root);

        var vm = _shown.ShouldHaveSingleItem();
        vm.InputPath.ShouldBe(_root);
        await vm.ProbeCommand.ExecutionTask.ShouldNotBeNull();
        vm.IsProbed.ShouldBeTrue();
        vm.Name.ShouldBe("Shop");
        vm.Tracks.ShouldNotBeEmpty("the tracks are loaded before the dialog shows");
        _dialogClosed.SetResult();
        await open;
    }

    [Fact]
    public async Task Add_workspace_without_a_path_opens_an_empty_dialog_and_detects_nothing()
    {
        var launcher = Launcher();

        var open = launcher.OpenAsync(null);

        var vm = _shown.ShouldHaveSingleItem();
        vm.InputPath.ShouldBeEmpty();
        vm.ProbeCommand.ExecutionTask.ShouldBeNull("nothing was detected");
        vm.ErrorMessage.ShouldBeNull();
        _dialogClosed.SetResult();
        await open;
    }
}
