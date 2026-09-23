using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CodeSwitchX.Hosting.Win32;

/// <summary>Positions, cloaks and focuses foreign top-level windows without reparenting them.</summary>
public sealed class SnapWindowDocker : IWindowDocker
{
    public void MoveTo(nint hwnd, ScreenRect rect)
    {
        var h = new HWND(hwnd);
        PInvoke.ShowWindow(h, SHOW_WINDOW_CMD.SW_RESTORE);
        PInvoke.SetWindowPos(h, HWND.Null, rect.Left, rect.Top, rect.Width, rect.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS);
    }

    public void Cloak(nint hwnd) => SetCloak(hwnd, cloak: true);

    public void Uncloak(nint hwnd) => SetCloak(hwnd, cloak: false);

    public void BringToFront(nint hwnd)
    {
        var h = new HWND(hwnd);
        PInvoke.SetWindowPos(h, HWND.HWND_TOP, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
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

    private static unsafe void SetCloak(nint hwnd, bool cloak)
    {
        var value = cloak ? 1 : 0;
        PInvoke.DwmSetWindowAttribute(new HWND(hwnd), DWMWINDOWATTRIBUTE.DWMWA_CLOAK, &value, sizeof(int));
    }
}
