using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Hosting.Win32;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
    public async Task Two_folders_with_the_same_name_opened_at_once_each_get_the_window_launched_for_them()
    {
        var windows = new List<WindowInfo>();
        var launchedFor = new Dictionary<Guid, nint>();
        _windows.TopLevelWindows().Returns(_ =>
        {
            lock (windows)
            {
                return windows.ToList();
            }
        });
        _launcher.Launch(Arg.Any<Workspace>()).Returns(call =>
        {
            lock (windows)
            {
                var hwnd = (nint)(800 + windows.Count);
                windows.Add(new WindowInfo(hwnd, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code"));
                launchedFor[call.Arg<Workspace>().Id] = hwnd;
            }

            return new LaunchResult(true, 1, null);
        });
        var fork = new Workspace { Name = "App (fork)", RootPath = @"c:\forks\app" };

        var results = await Task.WhenAll(_manager.OpenAsync(_workspace, CancellationToken.None), _manager.OpenAsync(fork, CancellationToken.None));

        launchedFor.Count.ShouldBe(2, "the second folder must not take the first one's window while it is still being found");
        results[0].Hwnd.ShouldBe(launchedFor[_workspace.Id]);
        results[1].Hwnd.ShouldBe(launchedFor[fork.Id]);
    }

    [Fact]
    public async Task When_two_unhosted_windows_name_the_workspace_the_one_vs_code_brings_forward_is_adopted()
    {
        // The same folder name in two places, or a floating editor window of this workspace: the title cannot say
        // which window shows c:\repo\app, but VS Code, asked to open it, brings that window forward.
        nint foreground = 42;
        _windows.ForegroundWindow().Returns(_ => foreground);
        _windows.TopLevelWindows().Returns([
            new WindowInfo(700, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code"),
            new WindowInfo(701, 30, "Chrome_WidgetWin_1", "Program.cs - app - Visual Studio Code"),
        ]);
        _launcher.Launch(_workspace).Returns(_ =>
        {
            foreground = 701;
            return new LaunchResult(true, 1234, null);
        });
        _windows.ProcessName(1234).Returns((string?)null);

        var hosted = await _manager.OpenAsync(_workspace, CancellationToken.None);

        _launcher.Received(1).Launch(_workspace);
        hosted.State.ShouldBe(HostState.Running);
        hosted.Hwnd.ShouldBe((nint)701);
        _docker.DidNotReceive().Cloak(Arg.Any<nint>());
    }

    [Fact]
    public async Task When_two_unhosted_windows_name_the_workspace_and_vs_code_brings_neither_forward_neither_is_adopted()
    {
        _windows.ForegroundWindow().Returns((nint)42);
        _windows.TopLevelWindows().Returns([
            new WindowInfo(700, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code"),
            new WindowInfo(701, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code"),
        ]);
        _launcher.Launch(_workspace).Returns(new LaunchResult(true, 1, null));

        var hosted = await _manager.OpenAsync(_workspace, CancellationToken.None);

        hosted.State.ShouldBe(HostState.Stopped);
        hosted.Error.ShouldNotBeNull().ShouldContain("2 VS Code windows");
    }

    [Fact]
    public async Task When_the_window_that_shows_the_folder_already_has_the_foreground_it_is_adopted_once_vs_code_has_answered()
    {
        // A jump hotkey pressed while typing in that very window: VS Code focuses it again, so nothing changes. Once
        // the Code.exe that was launched has handed the folder over and exited, the window in front is the answer.
        _windows.ForegroundWindow().Returns((nint)700);
        _windows.TopLevelWindows().Returns([
            new WindowInfo(700, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code"),
            new WindowInfo(701, 30, "Chrome_WidgetWin_1", "Other.cs - app - Visual Studio Code"),
        ]);
        _launcher.Launch(_workspace).Returns(new LaunchResult(true, 1234, null));
        _windows.ProcessName(1234).Returns((string?)null);

        var hosted = await _manager.OpenAsync(_workspace, CancellationToken.None);

        hosted.State.ShouldBe(HostState.Running);
        hosted.Hwnd.ShouldBe((nint)700);
    }

    [Fact]
    public async Task A_foreground_that_did_not_change_is_no_answer_while_vs_code_opens_a_new_window()
    {
        // The user types in c:\repo\app's window and jumps to c:\forks\app, which is not open: VS Code creates its
        // window (hidden, untitled at first) before the launched Code.exe exits, and that window is the answer.
        _windows.ForegroundWindow().Returns((nint)700);
        WindowInfo[] open = [new(700, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code"), new(701, 30, "Chrome_WidgetWin_1", "Other.cs - app - Visual Studio Code")];
        _windows.TopLevelWindows().Returns(
            open,
            [.. open, new WindowInfo(900, 30, "Chrome_WidgetWin_1", string.Empty) { IsVisible = false }],
            [.. open, new WindowInfo(900, 30, "Chrome_WidgetWin_1", string.Empty) { IsVisible = false }],
            [.. open, new WindowInfo(900, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code")]);
        _launcher.Launch(_workspace).Returns(new LaunchResult(true, 1234, null));
        _windows.ProcessName(1234).Returns((string?)null);

        var hosted = await _manager.OpenAsync(_workspace, CancellationToken.None);

        hosted.Hwnd.ShouldBe((nint)900);
    }

    [Fact]
    public async Task A_window_that_names_the_workspace_only_in_passing_is_not_taken_without_asking_vs_code()
    {
        // "App - Copy" (Explorer's name for a copied folder) is open; tile "app" is not. Its title has "app" as a
        // segment, but not where VS Code writes the folder name, so VS Code is launched and its new window adopted.
        _windows.TopLevelWindows().Returns(
            [new WindowInfo(700, 30, "Chrome_WidgetWin_1", "Program.cs - App - Copy - Visual Studio Code")],
            [new WindowInfo(700, 30, "Chrome_WidgetWin_1", "Program.cs - App - Copy - Visual Studio Code"), new WindowInfo(800, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code")]);
        _launcher.Launch(_workspace).Returns(new LaunchResult(true, 1234, null));

        var hosted = await _manager.OpenAsync(_workspace, CancellationToken.None);

        _launcher.Received(1).Launch(_workspace);
        hosted.Hwnd.ShouldBe((nint)800);
    }

    [Fact]
    public async Task Opening_returns_to_the_caller_before_the_launch_runs()
    {
        // OpenAsync is called on the UI thread. Launch checks the folder, and on a network share that is offline that
        // alone took 21 s: the shell must not freeze meanwhile.
        using var release = new ManualResetEventSlim();
        _windows.TopLevelWindows().Returns([]);
        _launcher.Launch(_workspace).Returns(_ =>
        {
            release.Wait(TimeSpan.FromSeconds(5));
            return new LaunchResult(false, null, @"\\nas\projects\app was not found");
        });

        var open = _manager.OpenAsync(_workspace, CancellationToken.None);

        open.IsCompleted.ShouldBeFalse();
        release.Set();
        (await open).State.ShouldBe(HostState.Stopped);
    }

    [Fact]
    public async Task A_floating_editor_window_of_a_tile_with_the_same_folder_name_is_not_taken_without_asking_vs_code()
    {
        // c:\repo\app is docked, and one of its editors floats in window 510. That title names "app" where VS Code
        // writes the folder name, like any window of c:\repo\app, so for c:\forks\app it proves nothing.
        _windows.TopLevelWindows().Returns([new WindowInfo(500, 30, "Chrome_WidgetWin_1", "Program.cs - app - Visual Studio Code")]);
        await _manager.OpenAsync(_workspace, CancellationToken.None);
        var fork = new Workspace { Name = "App (fork)", RootPath = @"c:\forks\app" };
        WindowInfo[] open = [new(500, 30, "Chrome_WidgetWin_1", "Program.cs - app - Visual Studio Code"), new(510, 30, "Chrome_WidgetWin_1", "Other.cs - app - Visual Studio Code")];
        _windows.TopLevelWindows().Returns(open, [.. open, new WindowInfo(800, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code")]);
        _launcher.Launch(fork).Returns(new LaunchResult(true, 1234, null));

        var hosted = await _manager.OpenAsync(fork, CancellationToken.None);

        _launcher.Received(1).Launch(fork);
        hosted.Hwnd.ShouldBe((nint)800);
    }

    [Fact]
    public async Task A_window_switched_to_a_folder_whose_name_contains_its_tiles_name_goes_to_that_folders_tile()
    {
        var app = new Workspace { Name = "App", RootPath = @"c:\repo\App" };
        var copy = new Workspace { Name = "App - Copy", RootPath = @"c:\repo\App - Copy" };
        _windows.TopLevelWindows().Returns([new WindowInfo(700, 30, "Chrome_WidgetWin_1", "App - Visual Studio Code")]);
        await _manager.OpenAsync(app, CancellationToken.None);
        _windows.TopLevelWindows().Returns([new WindowInfo(700, 30, "Chrome_WidgetWin_1", "Program.cs - App - Copy - Visual Studio Code")]);

        var hosted = await _manager.OpenAsync(copy, CancellationToken.None);

        _launcher.DidNotReceive().Launch(copy);
        hosted.Hwnd.ShouldBe((nint)700);
        _manager.Get(app.Id)!.State.ShouldBe(HostState.Stopped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_discovery_that_waited_behind_a_namesake_does_not_launch_once_its_tile_is_gone_or_CodeSwitchX_closes(bool closing)
    {
        _windows.TopLevelWindows().Returns([]);
        _launcher.Launch(Arg.Any<Workspace>()).Returns(new LaunchResult(true, 1234, null));
        var fork = new Workspace { Name = "App (fork)", RootPath = @"c:\forks\app" };

        var first = _manager.OpenAsync(_workspace, CancellationToken.None);
        var second = _manager.OpenAsync(fork, CancellationToken.None);
        if (closing)
        {
            _manager.ReleaseAll();
        }
        else
        {
            _manager.Forget(fork.Id);
        }

        await Task.WhenAll(first, second);

        _launcher.DidNotReceive().Launch(fork);
        (await second).State.ShouldBe(HostState.Stopped);
    }

    [Fact]
    public async Task A_window_the_user_switches_to_before_vs_code_has_answered_is_not_taken_for_its_answer()
    {
        // c:\repo\app is not open. While VS Code builds its window, the user Alt+Tabs to the other "app" window.
        nint foreground = 42;
        _windows.ForegroundWindow().Returns(_ => foreground);
        WindowInfo[] open = [new(700, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code"), new(701, 30, "Chrome_WidgetWin_1", "Other.cs - app - Visual Studio Code")];
        _windows.TopLevelWindows().Returns(open, open, open, [.. open, new WindowInfo(900, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code")]);
        _launcher.Launch(_workspace).Returns(_ =>
        {
            foreground = 701;
            return new LaunchResult(true, 1234, null);
        });
        _windows.ProcessName(1234).Returns("Code");

        var hosted = await _manager.OpenAsync(_workspace, CancellationToken.None);

        hosted.Hwnd.ShouldBe((nint)900);
    }

    [Fact]
    public async Task A_window_in_front_for_a_single_poll_is_no_answer()
    {
        // The user is in 701 as the launched Code.exe exits, and VS Code's focus on 700 lands a moment later.
        _windows.ForegroundWindow().Returns((nint)701, (nint)700);
        _windows.TopLevelWindows().Returns([
            new WindowInfo(700, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code"),
            new WindowInfo(701, 30, "Chrome_WidgetWin_1", "Other.cs - app - Visual Studio Code"),
        ]);
        _launcher.Launch(_workspace).Returns(new LaunchResult(true, 1234, null));
        _windows.ProcessName(1234).Returns((string?)null);

        var hosted = await _manager.OpenAsync(_workspace, CancellationToken.None);

        hosted.Hwnd.ShouldBe((nint)700);
    }

    [Fact]
    public async Task A_new_window_that_shows_another_folder_does_not_hide_vs_codes_answer()
    {
        // The jump hotkey is pressed while typing in 700; meanwhile another tile's window, or a floating editor, opens.
        _windows.ForegroundWindow().Returns((nint)700);
        WindowInfo[] open = [new(700, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code"), new(701, 30, "Chrome_WidgetWin_1", "Other.cs - app - Visual Studio Code")];
        _windows.TopLevelWindows().Returns(open, [.. open, new WindowInfo(902, 30, "Chrome_WidgetWin_1", "Other.cs - beta - Visual Studio Code")]);
        _launcher.Launch(_workspace).Returns(new LaunchResult(true, 1234, null));
        _windows.ProcessName(1234).Returns((string?)null);

        var hosted = await _manager.OpenAsync(_workspace, CancellationToken.None);

        hosted.State.ShouldBe(HostState.Running);
        hosted.Hwnd.ShouldBe((nint)700);
    }

    [Fact]
    public async Task A_hosted_window_that_now_shows_another_folder_is_adopted_by_that_folder_and_its_first_tile_stops()
    {
        var window = new WindowInfo(700, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code");
        _windows.TopLevelWindows().Returns([window]);
        await _manager.OpenAsync(_workspace, CancellationToken.None);
        var beta = new Workspace { Name = "Beta", RootPath = @"c:\repo\beta" };
        // File > Open Recent > beta in the docked window: VS Code opens it in that same window.
        _windows.TopLevelWindows().Returns([window with { Title = "beta - Visual Studio Code" }]);

        var hosted = await _manager.OpenAsync(beta, CancellationToken.None);

        _launcher.DidNotReceive().Launch(beta);
        hosted.State.ShouldBe(HostState.Running);
        hosted.Hwnd.ShouldBe((nint)700);
        _manager.Get(_workspace.Id)!.State.ShouldBe(HostState.Stopped);
        _changes[^2].ShouldBe(new HostStateChanged(_workspace.Id, HostState.Stopped, 700, "Its VS Code window now shows beta"));
    }

    [Fact]
    public void Hidden_vs_code_windows_left_by_an_earlier_run_are_shown_again()
    {
        _windows.ProcessName(40).Returns("chrome");
        _windows.TopLevelWindows().Returns([
            new WindowInfo(700, 30, "Chrome_WidgetWin_1", "Program.cs - app - Visual Studio Code") { IsVisible = false },
            new WindowInfo(701, 30, "Chrome_WidgetWin_1", "beta - Visual Studio Code"),
            new WindowInfo(702, 30, "Chrome_WidgetWin_1", string.Empty) { IsVisible = false },
            new WindowInfo(703, 40, "Chrome_WidgetWin_1", "app - Visual Studio Code") { IsVisible = false },
            new WindowInfo(704, 30, "Chrome_WidgetWin_0", "app - Visual Studio Code") { IsVisible = false },
        ]);

        new HiddenWindowSweep(_manager, NullLogger<HiddenWindowSweep>.Instance, anotherInstanceRuns: () => false).StartAsync(CancellationToken.None);

        _docker.Received(1).Uncloak(700);
        _docker.DidNotReceive().Uncloak(Arg.Is<nint>(h => h != 700));
    }

    [Fact]
    public void The_sweep_leaves_the_windows_of_another_running_instance_alone()
    {
        // A start that went on without the single-instance claim must not show the windows the running instance hides.
        _windows.TopLevelWindows().Returns([new WindowInfo(700, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code") { IsVisible = false }]);

        new HiddenWindowSweep(_manager, NullLogger<HiddenWindowSweep>.Instance, anotherInstanceRuns: () => true).StartAsync(CancellationToken.None);

        _docker.DidNotReceive().Uncloak(Arg.Any<nint>());
    }

    [Fact]
    public async Task A_sweep_that_fails_does_not_stop_CodeSwitchX_from_starting()
    {
        _windows.TopLevelWindows().Returns(_ => throw new InvalidOperationException("EnumWindows failed"));

        await new HiddenWindowSweep(_manager, NullLogger<HiddenWindowSweep>.Instance, anotherInstanceRuns: () => false).StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_sweep_leaves_the_windows_this_run_hides_alone()
    {
        WindowAppearsAfterLaunch();
        await _manager.OpenAsync(_workspace, CancellationToken.None);
        _windows.TopLevelWindows().Returns([new WindowInfo(500, 30, "Chrome_WidgetWin_1", "Program.cs - app - Visual Studio Code") { IsVisible = false }]);

        _manager.ShowOrphanedWindows();

        _docker.DidNotReceive().Uncloak(500);
    }

    [Fact]
    public async Task A_window_that_appears_after_ReleaseAll_is_neither_hidden_nor_adopted()
    {
        // CodeSwitchX closes while VS Code is still starting: nothing may hide the window after it was released.
        var window = new WindowInfo(500, 30, "Chrome_WidgetWin_1", "Program.cs - app - Visual Studio Code");
        _windows.TopLevelWindows().Returns(_ => [], _ =>
        {
            _manager.ReleaseAll();
            return [window];
        });
        _launcher.Launch(_workspace).Returns(new LaunchResult(true, 1, null));

        var hosted = await _manager.OpenAsync(_workspace, CancellationToken.None);

        _docker.DidNotReceive().Cloak(500);
        hosted.State.ShouldBe(HostState.Stopped);
    }

    [Fact]
    public async Task A_vs_code_window_that_runs_as_administrator_is_not_adopted_because_it_could_not_be_moved_or_hidden()
    {
        _windows.TopLevelWindows().Returns([new WindowInfo(700, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code [Administrator]")]);
        _docker.IsOutOfReach(700).Returns(true);

        var hosted = await _manager.OpenAsync(_workspace, CancellationToken.None);

        hosted.State.ShouldBe(HostState.Stopped);
        hosted.Error.ShouldNotBeNull().ShouldContain("administrator");
        _launcher.DidNotReceive().Launch(Arg.Any<Workspace>());
    }

    [Fact]
    public async Task SnapBack_gives_up_on_a_window_that_something_else_keeps_moving_to_the_same_place()
    {
        // A tiling window manager, or a second CodeSwitchX, puts the window back in its own place after every snap.
        var (manager, _) = ManagerWithFakeTime();
        _windows.TopLevelWindows().Returns([new WindowInfo(700, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code")]);
        await manager.OpenAsync(_workspace, CancellationToken.None);
        var cab = ScreenRect.FromSize(0, 28, 1600, 900);
        manager.ShowInCab(_workspace.Id, cab);
        _docker.GetRect(700).Returns(ScreenRect.FromSize(800, 0, 800, 1080));
        _docker.ClearReceivedCalls();

        for (var i = 0; i < 20; i++)
        {
            manager.SnapBack(700);
        }

        _docker.Received(HostManager.SnapBackLimit).MoveTo(700, cab);

        manager.Dock(_workspace.Id, cab);
        _docker.ClearReceivedCalls();
        manager.SnapBack(700);
        _docker.Received(1).MoveTo(700, cab);
    }

    [Fact]
    public async Task SnapBack_keeps_following_while_the_user_holds_a_drag_still()
    {
        // With the mouse held still, the move loop puts the window back under the cursor after every snap: the same
        // place again and again, as a tiling window manager would, but it is the user, and the last snap-back must win.
        var (manager, _) = ManagerWithFakeTime();
        _windows.TopLevelWindows().Returns([new WindowInfo(700, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code")]);
        await manager.OpenAsync(_workspace, CancellationToken.None);
        var cab = ScreenRect.FromSize(0, 28, 1600, 900);
        manager.ShowInCab(_workspace.Id, cab);
        _docker.GetRect(700).Returns(ScreenRect.FromSize(300, 200, 1600, 900));
        _docker.IsPrimaryButtonDown().Returns(true);
        _docker.ClearReceivedCalls();

        for (var i = 0; i < 20; i++)
        {
            manager.SnapBack(700);
        }

        _docker.IsPrimaryButtonDown().Returns(false);
        manager.SnapBack(700);

        _docker.Received(21).MoveTo(700, cab);
    }

    [Fact]
    public async Task SnapBack_follows_a_drag_and_a_window_moved_away_now_and_then()
    {
        var (manager, time) = ManagerWithFakeTime();
        _windows.TopLevelWindows().Returns([new WindowInfo(700, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code")]);
        await manager.OpenAsync(_workspace, CancellationToken.None);
        var cab = ScreenRect.FromSize(0, 28, 1600, 900);
        manager.ShowInCab(_workspace.Id, cab);
        _docker.ClearReceivedCalls();

        for (var i = 1; i <= 20; i++)
        {
            _docker.GetRect(700).Returns(ScreenRect.FromSize(i * 10, 28, 1600, 900));
            manager.SnapBack(700);
        }

        _docker.GetRect(700).Returns(ScreenRect.FromSize(800, 0, 800, 1080));
        for (var i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            manager.SnapBack(700);
        }

        _docker.Received(40).MoveTo(700, cab);
    }

    private (HostManager Manager, FakeTimeProvider Time) ManagerWithFakeTime()
    {
        var time = new FakeTimeProvider();
        return (new HostManager(_windows, _docker, _launcher, _bus, time, NullLogger<HostManager>.Instance), time);
    }

    [Fact]
    public async Task An_adopted_window_that_is_on_the_desktop_is_hidden_when_the_cab_shows_another_tile()
    {
        // AutoStart adopting the windows the previous run released: they are visible, and HideAll left them on the desktop.
        _windows.TopLevelWindows().Returns([new WindowInfo(700, 30, "Chrome_WidgetWin_1", "Program.cs - app - Visual Studio Code")]);
        var hosted = await _manager.OpenAsync(_workspace, CancellationToken.None);
        hosted.Visible.ShouldBeTrue("the window was adopted as it was: on the desktop");

        _manager.HideAll();

        _docker.Received(1).Cloak(700);
        hosted.Visible.ShouldBeFalse();
    }

    [Fact]
    public async Task Dock_shows_a_window_adopted_from_the_desktop_in_the_cab_the_first_time()
    {
        // The Cab's rectangle arrives before the shell raises the window: the first dock must do what ShowInCab does, or the
        // window stays behind the shell with the other desktop windows still there.
        _windows.TopLevelWindows().Returns([new WindowInfo(700, 30, "Chrome_WidgetWin_1", "Program.cs - app - Visual Studio Code")]);
        var hosted = await _manager.OpenAsync(_workspace, CancellationToken.None);
        hosted.Visible.ShouldBeTrue();
        var rect = ScreenRect.FromSize(0, 28, 1600, 900);

        _manager.Dock(_workspace.Id, rect);

        _docker.Received(1).MoveTo(700, rect);
        _docker.Received(1).BringToFront(700);
        hosted.TargetRect.ShouldBe(rect);
    }

    [Fact]
    public async Task A_hidden_window_that_vs_code_brought_forward_to_answer_counts_as_visible_when_adopted()
    {
        // The window was hidden when the windows were listed before the launch; VS Code showed it to answer. Adopted from the
        // old listing it would count as hidden, and HideAll would leave it on the desktop.
        _windows.ForegroundWindow().Returns((nint)700);
        WindowInfo[] before = [new(700, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code") { IsVisible = false }, new(701, 30, "Chrome_WidgetWin_1", "Other.cs - app - Visual Studio Code")];
        WindowInfo[] after = [new(700, 30, "Chrome_WidgetWin_1", "app - Visual Studio Code"), before[1]];
        _windows.TopLevelWindows().Returns(before, after);
        _launcher.Launch(_workspace).Returns(new LaunchResult(true, 1234, null));
        _windows.ProcessName(1234).Returns((string?)null);
        var hosted = await _manager.OpenAsync(_workspace, CancellationToken.None);
        hosted.Hwnd.ShouldBe((nint)700);
        hosted.Visible.ShouldBeTrue("VS Code brought it forward, so it is on the desktop now");

        _manager.HideAll();

        _docker.Received(1).Cloak(700);
    }

    [Fact]
    public async Task ShowInCab_moves_the_window_into_the_cab_before_it_shows_it()
    {
        // A fresh window is hidden where VS Code opened it, possibly on another monitor: shown first, it drew a frame there.
        WindowAppearsAfterLaunch();
        await _manager.OpenAsync(_workspace, CancellationToken.None);
        var rect = ScreenRect.FromSize(0, 28, 1600, 900);
        _docker.ClearReceivedCalls();

        _manager.ShowInCab(_workspace.Id, rect);

        Received.InOrder(() =>
        {
            _docker.MoveTo(500, rect);
            _docker.Uncloak(500);
            _docker.BringToFront(500);
        });
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
