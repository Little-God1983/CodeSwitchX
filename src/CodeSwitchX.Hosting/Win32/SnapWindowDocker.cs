using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CodeSwitchX.Hosting.Win32;

/// <summary>
/// Positions, hides and focuses foreign top-level windows without reparenting them. Every call that reaches into the
/// other process's message loop is asynchronous (ShowWindowAsync, SWP_ASYNCWINDOWPOS): these run on the WPF thread
/// under the host lock, and a stalled VS Code must not freeze the shell.
/// </summary>
public sealed class SnapWindowDocker : IWindowDocker
{
    public void MoveTo(nint hwnd, ScreenRect rect)
    {
        var h = new HWND(hwnd);
        if (PInvoke.IsIconic(h))
        {
            PInvoke.ShowWindowAsync(h, SHOW_WINDOW_CMD.SW_RESTORE);
        }

        PInvoke.SetWindowPos(h, HWND.Null, rect.Left, rect.Top, rect.Width, rect.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS);
    }

    /// <summary>
    /// Hides the window. DWM cloaking (DWMWA_CLOAK) is refused with E_ACCESSDENIED for windows of other processes,
    /// so this uses SW_HIDE, which also takes the window off the taskbar and out of Alt+Tab.
    /// </summary>
    public void Cloak(nint hwnd) => PInvoke.ShowWindowAsync(new HWND(hwnd), SHOW_WINDOW_CMD.SW_HIDE);

    public void Uncloak(nint hwnd)
    {
        var h = new HWND(hwnd);
        if (!PInvoke.IsWindowVisible(h))
        {
            PInvoke.ShowWindowAsync(h, SHOW_WINDOW_CMD.SW_SHOWNA);
        }
    }

    public void BringToFront(nint hwnd)
    {
        var h = new HWND(hwnd);
        PInvoke.SetWindowPos(h, HWND.HWND_TOP, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW | SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS);
        PInvoke.SetForegroundWindow(h);
    }

    public bool IsAlive(nint hwnd) => PInvoke.IsWindow(new HWND(hwnd));

    public ScreenRect? GetRect(nint hwnd)
    {
        if (!PInvoke.GetWindowRect(new HWND(hwnd), out var rect))
        {
            return null;
        }

        return new ScreenRect(rect.left, rect.top, rect.right, rect.bottom);
    }
}
