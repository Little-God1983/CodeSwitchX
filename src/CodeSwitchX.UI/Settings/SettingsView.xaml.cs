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

    private double? _levelPressedAt; // a volume slider's value when the mouse or a key went down on it

    /// <summary>A press on a volume slider, by the mouse or a key: what it is let go at is compared with this.</summary>
    private void OnLevelPressed(object sender, RoutedEventArgs e)
    {
        if (e is not KeyEventArgs { IsRepeat: true }) // a key held: its first press counts
        {
            _levelPressedAt = (sender as Slider)?.Value;
        }
    }

    /// <summary>A volume slider let go, its thumb or a click on its track (#305): it plays at the level just set.</summary>
    private void OnLevelLetGo(object sender, MouseButtonEventArgs e) => PlayLevel(sender);

    /// <summary>The keys that move a slider play it too, once each.</summary>
    private void OnLevelKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            PlayLevel(sender);
        }
    }

    /// <summary>Only a level that changed is played: a click on the thumb, or End at 100 %, plays nothing.</summary>
    private void PlayLevel(object sender)
    {
        var pressedAt = _levelPressedAt;
        _levelPressedAt = null;
        if (sender is Slider { Tag: ICommand play } slider && pressedAt is { } before && slider.Value != before && play.CanExecute(null))
        {
            play.Execute(null);
        }
    }

    /// <summary>
    /// The search box folded away with the sidebar (#162) while it had the keyboard: the pages' list takes it, so the next
    /// key is not lost.
    /// </summary>
    private void OnSearchVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false && SearchBox.IsKeyboardFocusWithin)
        {
            // The page shown, so the arrow keys go on from it.
            if (PagesList.ItemContainerGenerator.ContainerFromItem(PagesList.SelectedItem) is ListBoxItem selected)
            {
                selected.Focus();
            }
            else
            {
                PagesList.Focus();
            }
        }
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
