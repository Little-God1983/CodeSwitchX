using System.Windows;
using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.UI.Infrastructure;

internal static class WindowActivation
{
    private const uint VkLButton = 0x01;
    private const uint VkRButton = 0x02;
    private const uint VkMButton = 0x04;

    /// <summary>
    /// Whether a mouse button is held down right now, wherever the mouse is. WPF's own mouse state misses a press on the
    /// window's frame (its title bar), which is not client input.
    /// </summary>
    public static bool AnyMouseButtonDown() =>
        HotkeyInterop.IsKeyDown(VkLButton) || HotkeyInterop.IsKeyDown(VkRButton) || HotkeyInterop.IsKeyDown(VkMButton);

    /// <summary>Whether Shift, Ctrl, Alt or a Windows key is held right now, in whatever app has the keyboard.</summary>
    public static bool AnyModifierDown() =>
        HotkeyInterop.IsKeyDown(0x10) || HotkeyInterop.IsKeyDown(0x11) || HotkeyInterop.IsKeyDown(0x12) || HotkeyInterop.IsKeyDown(0x5B)
        || HotkeyInterop.IsKeyDown(0x5C);

    /// <summary>
    /// Brings the window back to the user: out of the minimised state, as it was before (maximized too, as from the
    /// taskbar), and to the front.
    /// </summary>
    public static void BringUp(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            // At once, not by a posted SC_RESTORE: what follows (a hotkey's workspace, Activate) needs it restored now.
            window.WindowState = window is Shell.IShellWindow { Restored: Shell.ShellWindowState.Maximized } ? WindowState.Maximized : WindowState.Normal;
        }

        window.Show();
        window.Activate();
    }
}
