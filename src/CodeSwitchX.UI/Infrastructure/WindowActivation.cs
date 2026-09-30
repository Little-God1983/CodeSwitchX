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

    /// <summary>Brings the window back to the user: out of the minimised state, and to the front.</summary>
    public static void BringUp(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
    }
}
