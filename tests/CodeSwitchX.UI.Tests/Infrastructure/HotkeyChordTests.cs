using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.UI.Infrastructure;

namespace CodeSwitchX.UI.Tests.Infrastructure;

public class HotkeyChordTests
{
    [Theory]
    [InlineData("Ctrl+Alt+F12", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x7Bu)]
    [InlineData("ctrl + alt + f3", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x72u)]
    [InlineData("Ctrl+Alt+PageDown", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x22u)]
    [InlineData("Ctrl+Shift+Alt+3", HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt, 0x33u)]
    [InlineData("Win+Alt+K", HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x4Bu)]
    public void A_chord_is_read_from_its_text(string text, HotkeyModifiers modifiers, uint key)
    {
        HotkeyChord.TryParse(text, out var chord).ShouldBeTrue();
        chord.ShouldBe(new HotkeyChord(modifiers, key));
    }

    [Theory]
    [InlineData("Ctrl+Alt+F12")]
    [InlineData("Ctrl+Shift+Alt+PageUp")]
    [InlineData("Alt+Win+Space")]
    [InlineData("Ctrl+Alt+NumPad3")]
    public void Its_text_reads_back_the_same(string text)
    {
        HotkeyChord.TryParse(text, out var chord).ShouldBeTrue();
        chord.Text.ShouldBe(text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Alt+Banana")]
    [InlineData("Ctrl+Ctrl+F3")]
    public void Anything_else_is_no_chord(string text)
    {
        HotkeyChord.TryParse(text, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Ctrl+Alt+F3", null)]
    [InlineData("F3", "Use Ctrl or Win with it: without them the key is taken from every app.")]
    [InlineData("Shift+F3", "Use Ctrl or Win with it: without them the key is taken from every app.")]
    [InlineData("Alt+F4", "Use Ctrl or Win with it: without them the key is taken from every app.")]
    [InlineData("Win+Alt+K", null)]
    [InlineData("Ctrl+V", "Use two of Ctrl, Shift, Alt and Win, one of them Ctrl or Win: apps use Ctrl+V and its like for themselves.")]
    [InlineData("Ctrl+F3", "Use two of Ctrl, Shift, Alt and Win, one of them Ctrl or Win: apps use Ctrl+F3 and its like for themselves.")]
    [InlineData("Ctrl+Alt+4", "AltGr counts as Ctrl+Alt: this would stop AltGr+4 from typing in every app. Add Shift.")]
    [InlineData("Ctrl+Alt+A", "AltGr counts as Ctrl+Alt: this would stop AltGr+A from typing in every app. Add Shift.")]
    [InlineData("Ctrl+Shift+Alt+A", null)]
    [InlineData("Ctrl+Alt+3", "AltGr counts as Ctrl+Alt: this would stop AltGr+3 from typing in every app. Add Shift.")]
    [InlineData("Ctrl+Alt+Q", "AltGr counts as Ctrl+Alt: this would stop AltGr+Q from typing in every app. Add Shift.")]
    [InlineData("Ctrl+Shift+Alt+3", null)]
    public void A_chord_that_would_break_typing_is_refused(string text, string? why)
    {
        HotkeyChord.TryParse(text, out var chord).ShouldBeTrue();
        chord.WhyNot.ShouldBe(why);
    }
}
