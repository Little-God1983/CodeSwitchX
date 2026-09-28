using System.Windows;

namespace CodeSwitchX.UI.Infrastructure;

internal static class WindowActivation
{
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
