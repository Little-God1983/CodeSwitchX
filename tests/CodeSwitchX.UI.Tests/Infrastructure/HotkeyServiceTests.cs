using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.UI.Infrastructure;

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
}
