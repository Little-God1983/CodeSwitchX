using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.UI.Shell;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Shell;

public class ShellViewModelTests
{
    private readonly ShellTestHarness _h = new();

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

        _h.Shell.OpenSettings();

        _h.Shell.Mode.ShouldBe(ShellMode.Settings);
        _h.Docker.Received(1).Cloak(500);
    }
}
