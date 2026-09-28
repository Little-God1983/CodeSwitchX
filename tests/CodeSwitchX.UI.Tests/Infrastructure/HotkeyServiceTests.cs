using System.Windows;
using System.Windows.Interop;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.UI.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

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
    [InlineData("Ctrl+Alt+Y")]
    [InlineData("Ctrl+Shift+Alt+9")]
    public async Task A_hotkey_brings_a_minimised_shell_back(string label)
    {
        // The Yard has one tile and no workspace was active, so neither hotkey switches anything here.
        var shell = new ShellTestHarness().Shell;
        var binding = HotkeyService.Bindings.Single(b => b.Label == label);

        await StaThread.RunAsync(() =>
        {
            var window = new Window
            {
                WindowState = WindowState.Minimized, ShowInTaskbar = false, ShowActivated = false,
                Left = -20000, Top = -20000, Width = 200, Height = 200,
            };
            window.Show();
            var hwnd = new WindowInteropHelper(window).Handle;
            var hotkeys = new HotkeyService(NullLogger<HotkeyService>.Instance);
            hotkeys.Attach(hwnd, shell);
            try
            {
                StaThread.SendMessage(hwnd, HotkeyInterop.WmHotkey, binding.Id, 0);

                window.WindowState.ShouldBe(WindowState.Normal, "a global hotkey acts on the shell, so the shell has to be on screen");
            }
            finally
            {
                hotkeys.Detach();
                window.Close();
            }
        });
    }
}
