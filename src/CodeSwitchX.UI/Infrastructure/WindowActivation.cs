using System.Runtime.InteropServices;
using System.Windows;

namespace CodeSwitchX.UI.Infrastructure;

internal static class WindowActivation
{
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int VkMButton = 0x04;

    /// <summary>
    /// Whether a mouse button is held down right now, wherever the mouse is. WPF's own mouse state misses a press on the
    /// window's frame (its title bar), which is not client input.
    /// </summary>
    public static bool AnyMouseButtonDown() => IsDown(VkLButton) || IsDown(VkRButton) || IsDown(VkMButton);

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

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
