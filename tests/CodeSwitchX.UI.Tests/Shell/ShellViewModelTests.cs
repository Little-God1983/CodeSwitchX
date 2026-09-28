using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.UI.Shell;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Shell;

public class ShellViewModelTests
{
    private const nint ShopHwnd = 700;
    private readonly ShellTestHarness _h = new();

    /// <summary>A second workspace whose VS Code window is open already, so opening it adopts that window. App's window never appears: its open ends Stopped.</summary>
    private Workspace AddShopWithItsWindowOpen()
    {
        var shop = new Workspace { Name = "Shop", RootPath = @"c:\repo\shop", TrackId = _h.General.Id };
        _h.Workspaces.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([_h.App, shop]));
        _h.Windows.TopLevelWindows().Returns([new WindowInfo(ShopHwnd, 31, "Chrome_WidgetWin_1", "Program.cs - Shop - Visual Studio Code")]);
        _h.Launcher.Launch(Arg.Any<Workspace>()).Returns(new LaunchResult(true, 1, null));
        return shop;
    }

    [Fact]
    public async Task EnterCab_switches_mode_opens_vscode_and_docks_into_the_known_rect()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        var rect = ScreenRect.FromSize(0, 28, 1600, 900);
        _h.Shell.Cab.LastHostRect = rect;

        await _h.Shell.EnterCabAsync(_h.App.Id);

        _h.Shell.Mode.ShouldBe(ShellMode.Cab);
        _h.Shell.ActiveWorkspaceId.ShouldBe(_h.App.Id);
        _h.Shell.Cab.ActiveTile!.Id.ShouldBe(_h.App.Id);
        _h.Host.Get(_h.App.Id)!.State.ShouldBe(HostState.Running);
        _h.Docker.Received(1).MoveTo(500, rect);
        _h.Shell.StatusMessage.ShouldBeNull();
    }

    [Fact]
    public async Task EnterCab_reports_launch_problems_in_the_status_message()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Windows.TopLevelWindows().Returns([]);
        _h.Launcher.Launch(Arg.Any<CodeSwitchX.Core.Workspaces.Workspace>()).Returns(new CodeSwitchX.Hosting.VsCode.LaunchResult(false, null, "code not found"));

        await _h.Shell.EnterCabAsync(_h.App.Id);

        _h.Shell.Mode.ShouldBe(ShellMode.Cab);
        _h.Shell.StatusMessage.ShouldBe("code not found");
    }

    [Fact]
    public async Task BackToYard_hides_windows_and_ToggleMode_round_trips()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        _h.Shell.Cab.LastHostRect = ScreenRect.FromSize(0, 0, 100, 100);
        await _h.Shell.EnterCabAsync(_h.App.Id);
        _h.Docker.ClearReceivedCalls(); // discovery cloaks once; only the Back cloak counts below

        _h.Shell.BackToYard();
        _h.Shell.Mode.ShouldBe(ShellMode.Yard);
        _h.Docker.Received(1).Cloak(500);

        _h.Shell.ToggleMode();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        _h.Shell.Mode.ShouldBe(ShellMode.Cab);
    }

    [Fact]
    public async Task UpdateCabRect_redocks_only_while_in_cab_mode()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        await _h.Shell.EnterCabAsync(_h.App.Id);
        var rect = ScreenRect.FromSize(10, 40, 800, 600);

        _h.Shell.UpdateCabRect(rect);
        _h.Docker.Received(1).MoveTo(500, rect);

        _h.Shell.BackToYard();
        _h.Shell.UpdateCabRect(ScreenRect.FromSize(0, 0, 50, 50));
        _h.Docker.DidNotReceive().MoveTo(500, ScreenRect.FromSize(0, 0, 50, 50));
    }

    [Fact]
    public async Task JumpTo_out_of_range_is_ignored_and_in_range_enters_the_cab()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();

        await _h.Shell.JumpToAsync(5);
        _h.Shell.Mode.ShouldBe(ShellMode.Yard);

        await _h.Shell.JumpToAsync(1);
        _h.Shell.Mode.ShouldBe(ShellMode.Cab);
    }

    [Fact]
    public async Task Settings_mode_hides_vscode_windows()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        _h.Shell.Cab.LastHostRect = ScreenRect.FromSize(0, 0, 100, 100);
        await _h.Shell.EnterCabAsync(_h.App.Id);
        _h.Docker.ClearReceivedCalls(); // discovery cloaks once; only the Settings cloak counts below

        _h.Shell.OpenSettings();

        _h.Shell.Mode.ShouldBe(ShellMode.Settings);
        _h.Docker.Received(1).Cloak(500);
    }

    [Fact]
    public async Task Workspaces_marked_AutoStart_are_launched_and_kept_hidden_at_startup()
    {
        _h.App.AutoStart = true;
        _h.VsCodeWindowAppears();

        await _h.Shell.InitializeAsync(CancellationToken.None);
        for (var i = 0; i < 100 && _h.Host.Get(_h.App.Id)?.State != HostState.Running; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        _h.Launcher.Received(1).Launch(_h.App);
        _h.Host.Get(_h.App.Id)!.State.ShouldBe(HostState.Running);
        _h.Shell.Mode.ShouldBe(ShellMode.Yard);
        _h.Docker.Received().Cloak(500);
        _h.Docker.DidNotReceive().Uncloak(500);
    }

    [Fact]
    public async Task Minimising_the_shell_in_cab_mode_hides_vscode_and_restoring_redocks_it()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        var rect = ScreenRect.FromSize(0, 28, 1600, 900);
        _h.Shell.Cab.LastHostRect = rect;
        await _h.Shell.EnterCabAsync(_h.App.Id);
        _h.Docker.ClearReceivedCalls();

        _h.Shell.SetShellMinimized(true);
        _h.Docker.Received(1).Cloak(500);

        var offScreen = ScreenRect.FromSize(-32000, -32000, 1600, 900);
        _h.Shell.UpdateCabRect(offScreen);
        _h.Docker.DidNotReceive().MoveTo(500, offScreen);

        _h.Shell.SetShellMinimized(false);
        _h.Docker.Received(1).Uncloak(500);
        _h.Docker.Received(1).MoveTo(500, rect);
        _h.Shell.Mode.ShouldBe(ShellMode.Cab);
    }

    [Fact]
    public async Task Opening_a_tile_while_its_autostart_discovery_is_still_running_docks_it_when_discovery_finishes()
    {
        _h.App.AutoStart = true;
        _h.VsCodeWindowAppears();
        var rect = ScreenRect.FromSize(0, 28, 1600, 900);
        _h.Shell.Cab.LastHostRect = rect;

        await _h.Shell.InitializeAsync(CancellationToken.None);
        await _h.Shell.EnterCabAsync(_h.App.Id);

        _h.Shell.StatusMessage.ShouldBeNull("an in-flight start is not a failure");
        _h.Shell.Mode.ShouldBe(ShellMode.Cab);
        _h.Launcher.Received(1).Launch(_h.App);
        _h.Docker.Received().MoveTo(500, rect);
    }

    [Fact]
    public async Task Resizing_the_cab_repositions_vscode_without_raising_it_and_activation_raises_it()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        var rect = ScreenRect.FromSize(0, 28, 1600, 900);
        _h.Shell.Cab.LastHostRect = rect;
        await _h.Shell.EnterCabAsync(_h.App.Id);
        _h.Docker.ClearReceivedCalls();
        var resized = ScreenRect.FromSize(0, 28, 1200, 700);

        _h.Shell.UpdateCabRect(resized);

        _h.Docker.Received(1).MoveTo(500, resized);
        _h.Docker.DidNotReceive().BringToFront(Arg.Any<nint>());

        _h.Shell.RaiseHostedWindow();

        _h.Docker.Received(1).BringToFront(500);
    }

    [Fact]
    public async Task Switching_inside_the_cab_to_a_workspace_that_still_has_to_start_hides_the_one_shown_so_far()
    {
        var shop = AddShopWithItsWindowOpen();
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Shell.Cab.LastHostRect = ScreenRect.FromSize(0, 28, 1600, 900);
        await _h.Shell.EnterCabAsync(shop.Id);
        _h.Host.Get(shop.Id)!.Visible.ShouldBeTrue();
        _h.Docker.ClearReceivedCalls(); // discovery cloaks once; only the switch counts below

        var openApp = _h.Shell.EnterCabAsync(_h.App.Id);

        _h.Docker.Received(1).Cloak(ShopHwnd);
        await openApp;
        _h.Host.Get(_h.App.Id)!.State.ShouldBe(HostState.Stopped);
        _h.Host.Get(shop.Id)!.Visible.ShouldBeFalse("the strip names App, so Shop's VS Code must not stay in the Cab under that name");
    }

    [Fact]
    public async Task An_open_that_fails_after_the_user_moved_on_leaves_the_status_of_the_workspace_now_shown_alone()
    {
        var shop = AddShopWithItsWindowOpen();
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Shell.Cab.LastHostRect = ScreenRect.FromSize(0, 28, 1600, 900);

        var openApp = _h.Shell.EnterCabAsync(_h.App.Id);
        await _h.Shell.EnterCabAsync(shop.Id);
        await openApp;

        _h.Host.Get(_h.App.Id)!.State.ShouldBe(HostState.Stopped);
        _h.Shell.ActiveWorkspaceId.ShouldBe(shop.Id);
        _h.Shell.StatusMessage.ShouldBeNull("App's error does not belong in Shop's strip");
    }

    [Fact]
    public async Task An_open_that_finishes_while_the_shell_is_minimised_shows_vscode_only_once_the_shell_is_restored()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        var rect = ScreenRect.FromSize(0, 28, 1600, 900);
        _h.Shell.Cab.LastHostRect = rect;
        _h.VsCodeWindowAppears();
        using var launchMayEnd = new ManualResetEventSlim();
        _h.Launcher.Launch(Arg.Any<Workspace>()).Returns(_ =>
        {
            launchMayEnd.Wait(TimeSpan.FromSeconds(10));
            return new LaunchResult(true, 1, null);
        });

        var open = _h.Shell.EnterCabAsync(_h.App.Id);
        _h.Shell.SetShellMinimized(true);
        launchMayEnd.Set();
        await open;

        _h.Host.Get(_h.App.Id)!.State.ShouldBe(HostState.Running);
        _h.Docker.DidNotReceive().Uncloak(500);
        _h.Docker.DidNotReceive().BringToFront(500);

        _h.Shell.SetShellMinimized(false);

        _h.Docker.Received(1).Uncloak(500);
        _h.Docker.Received(1).MoveTo(500, rect);
    }
}
