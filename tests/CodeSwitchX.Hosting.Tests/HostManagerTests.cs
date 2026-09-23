using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Hosting.Win32;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace CodeSwitchX.Hosting.Tests;

public class HostManagerTests
{
    private readonly IWindowEnumerator _windows = Substitute.For<IWindowEnumerator>();
    private readonly IWindowDocker _docker = Substitute.For<IWindowDocker>();
    private readonly IVsCodeLauncher _launcher = Substitute.For<IVsCodeLauncher>();
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly List<HostStateChanged> _changes = [];
    private readonly Workspace _workspace = new() { Name = "App", RootPath = @"c:\repo\app" };
    private readonly HostManager _manager;

    public HostManagerTests()
    {
        _bus.Subscribe<HostStateChanged>(_changes.Add);
        _windows.ProcessName(30).Returns("Code");
        _docker.IsAlive(Arg.Any<nint>()).Returns(true);
        _manager = new HostManager(_windows, _docker, _launcher, _bus, TimeProvider.System, NullLogger<HostManager>.Instance,
            new HostManagerOptions { DiscoveryTimeout = TimeSpan.FromMilliseconds(500), PollInterval = TimeSpan.FromMilliseconds(5) });
    }

    private void WindowAppearsAfterLaunch()
    {
        var before = new List<WindowInfo>();
        var after = new List<WindowInfo> { new(500, 30, "Chrome_WidgetWin_1", "Program.cs - app - Visual Studio Code") };
        _windows.TopLevelWindows().Returns(before, after);
        _launcher.Launch(_workspace).Returns(new LaunchResult(true, 1234, null));
    }

    [Fact]
    public async Task Open_launches_and_finds_the_new_window()
    {
        WindowAppearsAfterLaunch();

        var hosted = await _manager.OpenAsync(_workspace, CancellationToken.None);

        hosted.State.ShouldBe(HostState.Running);
        hosted.Hwnd.ShouldBe((nint)500);
        hosted.ProcessId.ShouldBe(30u);
        _changes.Select(c => c.State).ShouldBe([HostState.Starting, HostState.Running]);
        _manager.Get(_workspace.Id).ShouldBeSameAs(hosted);
    }

    [Fact]
    public async Task Open_is_a_no_op_while_the_window_is_alive()
    {
        WindowAppearsAfterLaunch();
        await _manager.OpenAsync(_workspace, CancellationToken.None);

        await _manager.OpenAsync(_workspace, CancellationToken.None);

        _launcher.Received(1).Launch(_workspace);
    }

    [Fact]
    public async Task Launch_failure_and_discovery_timeout_end_in_Stopped()
    {
        _windows.TopLevelWindows().Returns([]);
        _launcher.Launch(_workspace).Returns(new LaunchResult(false, null, "code not found"));
        (await _manager.OpenAsync(_workspace, CancellationToken.None)).State.ShouldBe(HostState.Stopped);
        _changes[^1].Error.ShouldBe("code not found");

        _launcher.Launch(_workspace).Returns(new LaunchResult(true, 1, null));
        var hosted = await _manager.OpenAsync(_workspace, CancellationToken.None);

        hosted.State.ShouldBe(HostState.Stopped);
        hosted.Error.ShouldNotBeNull().ShouldContain("window");
    }

    [Fact]
    public async Task ShowInCab_uncloaks_moves_and_hides_the_others()
    {
        WindowAppearsAfterLaunch();
        await _manager.OpenAsync(_workspace, CancellationToken.None);
        var other = new Workspace { Name = "Other", RootPath = @"c:\repo\other" };
        _windows.TopLevelWindows().Returns([], [new WindowInfo(600, 30, "Chrome_WidgetWin_1", "other - Visual Studio Code")]);
        _launcher.Launch(other).Returns(new LaunchResult(true, 2, null));
        await _manager.OpenAsync(other, CancellationToken.None);
        _docker.ClearReceivedCalls();
        var rect = ScreenRect.FromSize(0, 28, 1600, 900);

        _manager.ShowInCab(other.Id, rect);
        _manager.ShowInCab(_workspace.Id, rect);

        _docker.Received(1).Uncloak(500);
        _docker.Received(1).MoveTo(500, rect);
        _docker.Received(1).BringToFront(500);
        _docker.Received(1).Cloak(600);
        _manager.Get(_workspace.Id)!.Visible.ShouldBeTrue();
        _manager.Get(other.Id)!.Visible.ShouldBeFalse();
    }

    [Fact]
    public async Task HideAll_cloaks_visible_windows_and_SnapBack_restores_the_target_rect()
    {
        WindowAppearsAfterLaunch();
        await _manager.OpenAsync(_workspace, CancellationToken.None);
        var rect = ScreenRect.FromSize(0, 28, 1600, 900);
        _manager.ShowInCab(_workspace.Id, rect);
        _docker.ClearReceivedCalls();

        _docker.GetRect(500).Returns(ScreenRect.FromSize(50, 60, 1600, 900));
        _manager.SnapBack(500);
        _docker.Received(1).MoveTo(500, rect);

        _docker.GetRect(500).Returns(rect);
        _manager.SnapBack(500);
        _docker.Received(1).MoveTo(500, rect);

        _manager.HideAll();
        _docker.Received(1).Cloak(500);
        _manager.Get(_workspace.Id)!.Visible.ShouldBeFalse();
    }

    [Fact]
    public async Task Liveness_poll_marks_closed_windows_stopped()
    {
        WindowAppearsAfterLaunch();
        await _manager.OpenAsync(_workspace, CancellationToken.None);
        _docker.IsAlive(500).Returns(false);

        _manager.PollLiveness();

        _manager.Get(_workspace.Id)!.State.ShouldBe(HostState.Stopped);
        _changes[^1].State.ShouldBe(HostState.Stopped);
    }

    [Fact]
    public async Task Open_cloaks_the_discovered_window_until_it_is_shown_in_the_cab()
    {
        WindowAppearsAfterLaunch();

        await _manager.OpenAsync(_workspace, CancellationToken.None);

        _docker.Received(1).Cloak(500);
        _docker.DidNotReceive().Uncloak(500);
    }

    [Fact]
    public async Task ReleaseAll_and_Forget_uncloak_every_hosted_window_so_nothing_stays_invisible_after_exit()
    {
        WindowAppearsAfterLaunch();
        await _manager.OpenAsync(_workspace, CancellationToken.None);
        var other = new Workspace { Name = "Other", RootPath = @"c:\repo\other" };
        _windows.TopLevelWindows().Returns([], [new WindowInfo(600, 30, "Chrome_WidgetWin_1", "other - Visual Studio Code")]);
        _launcher.Launch(other).Returns(new LaunchResult(true, 2, null));
        await _manager.OpenAsync(other, CancellationToken.None);
        _manager.ShowInCab(_workspace.Id, ScreenRect.FromSize(0, 0, 100, 100));
        _manager.HideAll();
        _docker.ClearReceivedCalls();

        _manager.ReleaseAll();

        _docker.Received(1).Uncloak(500);
        _docker.Received(1).Uncloak(600);
        _manager.All.ShouldAllBe(h => h.Visible);

        _docker.ClearReceivedCalls();
        _manager.HideAll();
        _manager.Forget(other.Id);

        _docker.Received(1).Uncloak(600);
        _manager.Get(other.Id).ShouldBeNull();
    }

    [Fact]
    public async Task Open_adopts_a_vscode_window_that_already_shows_the_workspace_instead_of_launching()
    {
        _windows.TopLevelWindows().Returns([new WindowInfo(700, 30, "Chrome_WidgetWin_1", "Program.cs - app - Visual Studio Code")]);

        var hosted = await _manager.OpenAsync(_workspace, CancellationToken.None);

        hosted.State.ShouldBe(HostState.Running);
        hosted.Hwnd.ShouldBe((nint)700);
        _launcher.DidNotReceive().Launch(Arg.Any<Workspace>());
    }

    [Fact]
    public async Task A_failure_during_discovery_ends_in_Stopped_and_the_workspace_can_be_opened_again()
    {
        _windows.TopLevelWindows().Returns(_ => [], _ => throw new InvalidOperationException("EnumWindows failed"));
        _launcher.Launch(_workspace).Returns(new LaunchResult(true, 1, null));

        var hosted = await _manager.OpenAsync(_workspace, CancellationToken.None);

        hosted.State.ShouldBe(HostState.Stopped);
        hosted.Error.ShouldNotBeNull().ShouldContain("EnumWindows failed");

        _windows.ClearSubstitute(ClearOptions.ReturnValues); // NSubstitute would otherwise run the configured throw while re-specifying
        _windows.ProcessName(30).Returns("Code");
        WindowAppearsAfterLaunch();
        (await _manager.OpenAsync(_workspace, CancellationToken.None)).State.ShouldBe(HostState.Running);
    }

    [Fact]
    public async Task Forgetting_a_workspace_during_discovery_leaves_the_late_window_uncloaked_and_untracked()
    {
        var window = new WindowInfo(500, 30, "Chrome_WidgetWin_1", "Program.cs - app - Visual Studio Code");
        _windows.TopLevelWindows().Returns(_ => [], _ =>
        {
            _manager.Forget(_workspace.Id);
            return [window];
        });
        _launcher.Launch(_workspace).Returns(new LaunchResult(true, 1, null));

        var hosted = await _manager.OpenAsync(_workspace, CancellationToken.None);

        hosted.State.ShouldBe(HostState.Stopped);
        _docker.DidNotReceive().Cloak(500);
        _manager.Get(_workspace.Id).ShouldBeNull();
    }

    [Fact]
    public async Task Concurrent_open_calls_share_one_discovery_and_both_end_running()
    {
        WindowAppearsAfterLaunch();

        var first = _manager.OpenAsync(_workspace, CancellationToken.None);
        var second = _manager.OpenAsync(_workspace, CancellationToken.None);
        second.IsCompleted.ShouldBeFalse("the second caller must wait for the discovery in flight instead of getting the Starting record back");
        var results = await Task.WhenAll(first, second);

        results[0].State.ShouldBe(HostState.Running);
        results[1].State.ShouldBe(HostState.Running, "a second caller must wait for the discovery in flight, not be told it failed");
        _launcher.Received(1).Launch(_workspace);
    }

    [Fact]
    public async Task Unregistering_a_workspace_uncloaks_and_forgets_its_window()
    {
        WindowAppearsAfterLaunch();
        await _manager.OpenAsync(_workspace, CancellationToken.None);
        _docker.ClearReceivedCalls();

        _bus.Publish(new WorkspaceUnregistered(_workspace.Id));

        _docker.Received(1).Uncloak(500);
        _manager.Get(_workspace.Id).ShouldBeNull();
    }

    [Fact]
    public async Task A_window_hosted_by_another_workspace_is_never_adopted_even_when_the_folders_share_a_name()
    {
        _windows.TopLevelWindows().Returns([new WindowInfo(700, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code")]);
        await _manager.OpenAsync(_workspace, CancellationToken.None);
        var twin = new Workspace { Name = "App (fork)", RootPath = @"c:\forks\app" };
        var before = new List<WindowInfo> { new(700, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code") };
        var after = new List<WindowInfo> { before[0], new(800, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code") };
        _windows.TopLevelWindows().Returns(before, after);
        _launcher.Launch(twin).Returns(new LaunchResult(true, 2, null));

        var hosted = await _manager.OpenAsync(twin, CancellationToken.None);

        _launcher.Received(1).Launch(twin);
        hosted.Hwnd.ShouldBe((nint)800);
        _manager.Get(_workspace.Id)!.Hwnd.ShouldBe((nint)700);
    }

    [Fact]
    public async Task Dock_repositions_a_visible_window_without_raising_it_again()
    {
        WindowAppearsAfterLaunch();
        await _manager.OpenAsync(_workspace, CancellationToken.None);
        var first = ScreenRect.FromSize(0, 28, 1600, 900);
        var second = ScreenRect.FromSize(0, 28, 1200, 700);
        _manager.ShowInCab(_workspace.Id, first);
        _docker.ClearReceivedCalls();

        _manager.Dock(_workspace.Id, second);

        _docker.Received(1).MoveTo(500, second);
        _docker.DidNotReceive().BringToFront(Arg.Any<nint>());
        _docker.DidNotReceive().Uncloak(Arg.Any<nint>());
        _manager.Get(_workspace.Id)!.TargetRect.ShouldBe(second);
    }

    [Fact]
    public async Task Dock_shows_a_hidden_window_the_same_way_ShowInCab_does()
    {
        WindowAppearsAfterLaunch();
        await _manager.OpenAsync(_workspace, CancellationToken.None);
        var rect = ScreenRect.FromSize(0, 28, 1600, 900);
        _docker.ClearReceivedCalls();

        _manager.Dock(_workspace.Id, rect);

        _docker.Received(1).Uncloak(500);
        _docker.Received(1).BringToFront(500);
        _manager.Get(_workspace.Id)!.Visible.ShouldBeTrue();
    }
}
