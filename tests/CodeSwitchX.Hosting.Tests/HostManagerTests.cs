using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Hosting.Win32;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

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
        hosted.Error.ShouldContain("window");
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
}
