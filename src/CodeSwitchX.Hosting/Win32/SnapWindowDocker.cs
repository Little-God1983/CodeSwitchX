using System.Runtime.InteropServices;
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
    private const int ErrorAccessDenied = 5;
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;

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

    /// <summary>
    /// Shows the window whether or not it looks visible: a hide posted by <see cref="Cloak"/> that VS Code has not
    /// processed yet leaves it visible for now, and would land after this. Posted in order, the show comes last.
    /// </summary>
    public void Uncloak(nint hwnd) => PInvoke.ShowWindowAsync(new HWND(hwnd), SHOW_WINDOW_CMD.SW_SHOWNA);

    public void BringToFront(nint hwnd)
    {
        var h = new HWND(hwnd);
        PInvoke.SetWindowPos(h, HWND.HWND_TOP, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW | SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS);
        PInvoke.SetForegroundWindow(h);
    }

    public bool IsAlive(nint hwnd) => PInvoke.IsWindow(new HWND(hwnd));

    /// <summary>
    /// A move that changes nothing: Windows checks the right to move the window all the same, and says
    /// ERROR_ACCESS_DENIED. It does so at the call, before anything is posted: checked against an elevated app's
    /// windows from a process that is not, with and without SWP_ASYNCWINDOWPOS.
    /// </summary>
    public bool IsOutOfReach(nint hwnd)
    {
        if (PInvoke.SetWindowPos(new HWND(hwnd), HWND.Null, 0, 0, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
                | SET_WINDOW_POS_FLAGS.SWP_NOSENDCHANGING | SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS))
        {
            return false;
        }

        return Marshal.GetLastPInvokeError() == ErrorAccessDenied;
    }

    /// <summary>GetAsyncKeyState reads the physical buttons: with the buttons swapped, the primary one is the right one.</summary>
    public bool IsPrimaryButtonDown() =>
        PInvoke.GetAsyncKeyState(PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_SWAPBUTTON) != 0 ? VkRButton : VkLButton) < 0;

    public ScreenRect? GetRect(nint hwnd)
    {
        if (!PInvoke.GetWindowRect(new HWND(hwnd), out var rect))
        {
            return null;
        }

        return new ScreenRect(rect.left, rect.top, rect.right, rect.bottom);
    }
}
