using System.Windows;
using System.Windows.Interop;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Shell;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Infrastructure;

public class HotkeyServiceTests
{
    [Fact]
    public void Digit_hotkeys_never_use_bare_ctrl_alt_because_that_is_what_altgr_sends()
    {
        var digits = HotkeyService.Bindings.Where(b => b.VirtualKey is >= 0x31 and <= 0x39).ToList();

        digits.Count.ShouldBe(9);
        foreach (var binding in digits)
        {
            var modifiers = binding.Modifiers & ~HotkeyModifiers.NoRepeat;
            modifiers.HasFlag(HotkeyModifiers.Control | HotkeyModifiers.Alt).ShouldBeTrue(binding.Label);
            modifiers.ShouldNotBe(HotkeyModifiers.Control | HotkeyModifiers.Alt, binding.Label + " would swallow AltGr+digit on German keyboards");
        }
    }

    [Fact]
    public void Toggle_hotkey_stays_ctrl_alt_y()
    {
        var toggle = HotkeyService.Bindings.Single(b => b.VirtualKey == 0x59);

        (toggle.Modifiers & ~HotkeyModifiers.NoRepeat).ShouldBe(HotkeyModifiers.Control | HotkeyModifiers.Alt);
        toggle.Label.ShouldBe("Ctrl+Alt+Y");
    }

    [Theory]
    [InlineData("Ctrl+Alt+Y")] // no workspace was opened yet, so there is nothing to toggle to
    [InlineData("Ctrl+Shift+Alt+9")] // the Yard has one tile
    public async Task A_hotkey_with_nothing_to_do_leaves_a_minimised_shell_where_it_is(string label)
    {
        var harness = new ShellTestHarness();
        await harness.Shell.InitializeAsync(CancellationToken.None);

        await OnMinimisedShellAsync(harness, label, window =>
            window.WindowState.ShouldBe(WindowState.Minimized, "the user's own application keeps the foreground"));
    }

    [Fact]
    public async Task A_hotkey_acts_before_the_shell_comes_back_so_the_restore_does_not_dock_the_workspace_it_leaves()
    {
        var harness = new ShellTestHarness();
        await harness.Shell.InitializeAsync(CancellationToken.None);
        harness.VsCodeWindowAppears();
        harness.Shell.Cab.LastHostRect = ScreenRect.FromSize(0, 28, 1600, 900);
        await harness.Shell.EnterCabAsync(harness.App.Id);

        await OnMinimisedShellAsync(harness, "Ctrl+Alt+Y", window =>
        {
            window.WindowState.ShouldBe(WindowState.Normal, "a hotkey that acts brings the shell back");
            harness.Shell.Mode.ShouldBe(ShellMode.Yard);
            harness.Docker.DidNotReceive().Uncloak(500);
        });
    }

    /// <summary>
    /// Minimises a window wired to the shell the way MainWindow is, presses the hotkey, and checks the result on the
    /// window's own thread.
    /// </summary>
    private static Task OnMinimisedShellAsync(ShellTestHarness harness, string label, Action<Window> check)
    {
        var binding = HotkeyService.Bindings.Single(b => b.Label == label);
        return StaThread.RunAsync(() =>
        {
            var window = new Window
            {
                WindowState = WindowState.Minimized, ShowInTaskbar = false, ShowActivated = false,
                Left = -20000, Top = -20000, Width = 200, Height = 200,
            };
            window.Show();
            window.StateChanged += (_, _) => harness.Shell.SetShellMinimized(window.WindowState == WindowState.Minimized);
            harness.Shell.SetShellMinimized(true);
            harness.Docker.ClearReceivedCalls();
            var hwnd = new WindowInteropHelper(window).Handle;
            var hotkeys = new HotkeyService(NullLogger<HotkeyService>.Instance);
            hotkeys.Attach(hwnd, harness.Shell);
            try
            {
                StaThread.SendMessage(hwnd, HotkeyInterop.WmHotkey, binding.Id, 0);

                check(window);
            }
            finally
            {
                hotkeys.Detach();
                window.Close();
            }
        });
    }
}
