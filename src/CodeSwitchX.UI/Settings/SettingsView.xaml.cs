using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.UI.Infrastructure;

namespace CodeSwitchX.UI.Settings;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>A chord box has the keyboard: the chat hotkeys are let go, so pressing one reaches the box.</summary>
    private void OnChordFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is SettingsViewModel settings)
        {
            settings.Shortcuts.Capturing = true;
        }
    }

    private void OnChordFocusLost(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is SettingsViewModel settings)
        {
            settings.Shortcuts.Capturing = false;
        }
    }

    /// <summary>
    /// The chord pressed in a box becomes its text, and the row's hotkey at once; Backspace or Delete alone clears it, and
    /// Tab moves on. A modifier alone waits for its key.
    /// </summary>
    private void OnChordKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ChatHotkeyRow row })
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (key == Key.Tab && modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            return;
        }

        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        if (key is Key.Back or Key.Delete && modifiers == ModifierKeys.None)
        {
            row.Chord = "";
            return;
        }

        var chord = new HotkeyChord(
            (modifiers.HasFlag(ModifierKeys.Control) ? HotkeyModifiers.Control : 0)
            | (modifiers.HasFlag(ModifierKeys.Shift) ? HotkeyModifiers.Shift : 0)
            | (modifiers.HasFlag(ModifierKeys.Alt) ? HotkeyModifiers.Alt : 0)
            | (modifiers.HasFlag(ModifierKeys.Windows) ? HotkeyModifiers.Win : 0),
            (uint)KeyInterop.VirtualKeyFromKey(key));
        if (HotkeyChord.NameOf(chord.VirtualKey) is null)
        {
            return; // a key no chord is set to (Enter, a punctuation key): the box keeps what it has
        }

        row.Chord = chord.Text;
    }
}
