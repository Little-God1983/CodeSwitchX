using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CodeSwitchX.Hosting.Win32;

/// <summary>
/// Positions, hides and focuses foreign top-level windows without reparenting them. Every call that reaches into the
/// other process's message loop is asynchronous (ShowWindowAsync, SWP_ASYNCWINDOWPOS): these run on the WPF thread
/// under the host lock, and a stalled VS Code must not freeze the shell.
/// <para>
/// One exception: <see cref="MoveTo"/> of a minimized or maximized window calls SetWindowPlacement, which waits for
/// VS Code, and a snap-back reaches it right after the user maximized the docked window (a double click on its title
/// bar, Win+Up). VS Code has just handled that maximize, so it is answering; a VS Code that stalls in between holds the
/// shell until it answers. Nothing else takes the window out of either state while moving it, and the mouse hook does
/// not wait for the host lock, so the rest of the desktop is not held with it.
/// </para>
/// </summary>
public sealed class SnapWindowDocker : IWindowDocker
{
    private const int ErrorAccessDenied = 5;

    private readonly ILogger<SnapWindowDocker>? _logger;
    private readonly Func<nint, bool> _isInMoveLoop;

    public SnapWindowDocker(ILogger<SnapWindowDocker>? logger = null)
        : this(logger, IsInMoveLoop)
    {
    }

    /// <param name="isInMoveLoop">Whether the window is being dragged or resized right now; the real check in production, a fixed answer in tests.</param>
    internal SnapWindowDocker(ILogger<SnapWindowDocker>? logger, Func<nint, bool> isInMoveLoop)
    {
        _logger = logger;
        _isInMoveLoop = isInMoveLoop;
    }

    /// <summary>
    /// Moves the window to the rect in its normal state. A maximized window is shown by the move, hidden or not: the one
    /// move of a hidden window is the Cab's, which shows it right after.
    /// </summary>
    public void MoveTo(nint hwnd, ScreenRect rect)
    {
        var h = new HWND(hwnd);
        var minimized = PInvoke.IsIconic(h);
        if (minimized || PInvoke.IsZoomed(h))
        {
            // A minimized window keeps a restore rectangle, and SW_RESTORE showed it there (another monitor, say) before the
            // move landed. The placement sets that rectangle while the window is minimized: a hidden one stays hidden and comes
            // back there once it is uncloaked and moved again, a visible one is restored there without activation.
            // A maximized window stays maximized when moved, laid over its whole monitor, the shell's Yard button included; the
            // placement is also what takes it out of that state (checked on screen with VS Code: while hidden, it does not).
            // SetWindowPlacement is synchronous; it is the one way to do either.
            var placement = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
            var visible = PInvoke.IsWindowVisible(h);
            if (PInvoke.GetWindowPlacement(h, ref placement))
            {
                var normal = IsToolWindow(h) ? rect : ToWorkspace(rect, WorkAreaOffset(rect));
                placement.rcNormalPosition = new RECT { left = normal.Left, top = normal.Top, right = normal.Right, bottom = normal.Bottom };
                placement.showCmd = visible || !minimized ? SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE : SHOW_WINDOW_CMD.SW_HIDE;
                placement.flags = 0;
                PInvoke.SetWindowPlacement(h, in placement);
            }

            if (minimized && !visible)
            {
                return;
            }
        }

        PInvoke.SetWindowPos(h, HWND.Null, rect.Left, rect.Top, rect.Width, rect.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS);
    }

    /// <summary>
    /// The placement's normal rectangle is in workspace coordinates for a window that is not a tool window, as VS Code's
    /// are not: relative to the work area, so a taskbar at the top or on the left shifts it. Given in screen coordinates,
    /// the window was restored that far off the Cab, and shown there, before the move after it landed.
    /// </summary>
    internal static ScreenRect ToWorkspace(ScreenRect rect, (int X, int Y) workAreaOffset) =>
        new(rect.Left - workAreaOffset.X, rect.Top - workAreaOffset.Y, rect.Right - workAreaOffset.X, rect.Bottom - workAreaOffset.Y);

    /// <summary>How far the work area of the monitor the rect lands on starts from that monitor's corner.</summary>
    private static (int X, int Y) WorkAreaOffset(ScreenRect rect)
    {
        var monitor = PInvoke.MonitorFromRect(new RECT { left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom },
            MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        return PInvoke.GetMonitorInfo(monitor, ref info)
            ? (info.rcWork.left - info.rcMonitor.left, info.rcWork.top - info.rcMonitor.top)
            : (0, 0);
    }

    private static bool IsToolWindow(HWND h) =>
        ((WINDOW_EX_STYLE)PInvoke.GetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE) & WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0;

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

    public void PlaceOnTop(nint hwnd) =>
        PInvoke.SetWindowPos(new HWND(hwnd), HWND.HWND_TOP, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW
            | SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS);

    public void PlaceUnder(nint hwnd, nint above) =>
        PInvoke.SetWindowPos(new HWND(hwnd), new HWND(above), 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW
            | SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS);

    /// <summary>
    /// A drag or resize by the frame runs in Windows' modal move loop, inside the window's own thread, which holds the
    /// mouse capture for as long as it lasts. WM_CANCELMODE makes DefWindowProc release that capture, and the loop ends
    /// where it is: at its start, before it has moved anything. Delivered as a notification: a stalled VS Code would
    /// otherwise hold the WPF thread, in a WinEvent callback.
    /// </summary>
    public void CancelMoveSize(nint hwnd)
    {
        // The event that asks for this was queued to the WPF thread. A click on the title bar starts and ends its loop
        // within the time a busy shell takes to get here, and a cancel sent then would end whatever the user does next
        // in the editor: a text selection, a tab drag. Only a loop still running is ended.
        if (!_isInMoveLoop(hwnd))
        {
            return;
        }

        if (!PInvoke.SendNotifyMessage(new HWND(hwnd), PInvoke.WM_CANCELMODE, default, default))
        {
            // Nothing else would say why a drag went ahead and fought the snap-back.
            _logger?.LogWarning("Windows refused to end the drag of window {Hwnd} (error {Error}); it is snapped back instead", hwnd, Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>Windows' move loop runs on the thread of the window it moves, and that thread's GUI state names the window while it does.</summary>
    private static bool IsInMoveLoop(nint hwnd)
    {
        var h = new HWND(hwnd);
        var thread = PInvoke.GetWindowThreadProcessId(h, out _);
        var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        return thread != 0 && PInvoke.GetGUIThreadInfo(thread, ref info)
            && (info.flags & GUITHREADINFO_FLAGS.GUI_INMOVESIZE) != 0 && info.hwndMoveSize == h;
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

    public ScreenRect? GetRect(nint hwnd)
    {
        if (!PInvoke.GetWindowRect(new HWND(hwnd), out var rect))
        {
            return null;
        }

        return new ScreenRect(rect.left, rect.top, rect.right, rect.bottom);
    }
}
