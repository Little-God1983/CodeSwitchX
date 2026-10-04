using System.Globalization;
using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>
/// A key chord a global hotkey is set to, as the user writes and reads it: "Ctrl+Alt+F3". Modifiers in the order Ctrl,
/// Shift, Alt, Win, then one key: a letter, a digit, F1–F24, NumPad0–9, Space, PageUp, PageDown, Home, End, Insert,
/// Delete or an arrow.
/// </summary>
public sealed record HotkeyChord(HotkeyModifiers Modifiers, uint VirtualKey)
{
    private static readonly (string Name, uint Key)[] Named =
    [
        ("Space", 0x20), ("PageUp", 0x21), ("PageDown", 0x22), ("End", 0x23), ("Home", 0x24), ("Left", 0x25), ("Up", 0x26),
        ("Right", 0x27), ("Down", 0x28), ("Insert", 0x2D), ("Delete", 0x2E),
    ];

    /// <summary>
    /// Whether AltGr types a character with the key on some layout: every digit and letter does on one or another
    /// (German @ € µ ² { [ ] }, French { [ | on 4 5 6, Polish ą ę ł on letters).
    /// </summary>
    private static bool AltGrTypes(uint key) => key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A;

    private const HotkeyModifiers Chord = HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt | HotkeyModifiers.Win;

    public static bool TryParse(string? text, out HotkeyChord chord)
    {
        chord = null!;
        var parts = (text ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 1)
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" or "STRG" => HotkeyModifiers.Control,
                "SHIFT" => HotkeyModifiers.Shift,
                "ALT" => HotkeyModifiers.Alt,
                "WIN" => HotkeyModifiers.Win,
                _ => HotkeyModifiers.None,
            };
            if (modifier == HotkeyModifiers.None || modifiers.HasFlag(modifier))
            {
                return false;
            }

            modifiers |= modifier;
        }

        if (KeyOf(parts[^1]) is not { } key)
        {
            return false;
        }

        chord = new HotkeyChord(modifiers, key);
        return true;
    }

    /// <summary>The virtual key a name stands for; null for a name not taken.</summary>
    private static uint? KeyOf(string name)
    {
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
        {
            return char.ToUpperInvariant(name[0]);
        }

        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 24)
        {
            return (uint)(0x70 + f - 1);
        }

        if (name.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && name.Length == 7 && char.IsAsciiDigit(name[6]))
        {
            return (uint)(0x60 + name[6] - '0');
        }

        return Named.FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Name: not null } named ? named.Key : null;
    }

    /// <summary>The name of a key a chord can use; null for any other, which a chord cannot be set to.</summary>
    public static string? NameOf(uint key) => key switch
    {
        >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 => ((char)key).ToString(),
        >= 0x70 and <= 0x87 => "F" + (key - 0x70 + 1).ToString(CultureInfo.InvariantCulture),
        >= 0x60 and <= 0x69 => "NumPad" + (key - 0x60).ToString(CultureInfo.InvariantCulture),
        _ => Named.FirstOrDefault(n => n.Key == key).Name,
    };

    /// <summary>"Ctrl+Alt+F3": what <see cref="TryParse"/> reads back.</summary>
    public string Text
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) { parts.Add("Ctrl"); }
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) { parts.Add("Shift"); }
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) { parts.Add("Alt"); }
            if (Modifiers.HasFlag(HotkeyModifiers.Win)) { parts.Add("Win"); }
            parts.Add(NameOf(VirtualKey) ?? "?");
            return string.Join("+", parts);
        }
    }

    /// <summary>
    /// Why the chord cannot be a global hotkey, in words for Settings; null when it can. It needs Ctrl or Win: a key
    /// alone, with Shift or with Alt alone (Alt+F4, Alt+Space, a menu's Alt+letter) is one every app uses, and so is one
    /// with a single modifier (Ctrl+V, Ctrl+F4): it needs two. AltGr counts as Ctrl+Alt, so Ctrl+Alt with a digit or a
    /// letter would stop the character AltGr types with it in every app (see <see cref="HotkeyService"/>).
    /// </summary>
    public string? WhyNot =>
        (Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Win)) == 0
            ? "Use Ctrl or Win with it: without them the key is taken from every app."
            : System.Numerics.BitOperations.PopCount((uint)(Modifiers & Chord)) < 2
                ? $"Use two of Ctrl, Shift, Alt and Win, one of them Ctrl or Win: apps use {Text} and its like for themselves."
                : (Modifiers & Chord) == (HotkeyModifiers.Control | HotkeyModifiers.Alt) && AltGrTypes(VirtualKey)
                    ? $"AltGr counts as Ctrl+Alt: this would stop AltGr+{NameOf(VirtualKey)} from typing in every app. Add Shift."
                    : null;
}
