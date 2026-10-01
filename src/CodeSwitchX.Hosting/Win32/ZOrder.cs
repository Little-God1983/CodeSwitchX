using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CodeSwitchX.Hosting.Win32;

/// <summary>
/// Keeps the shell under the VS Code window docked in its Cab. Activating a window brings it to the front: a click on
/// the shell put the shell over VS Code until VS Code was raised again, so VS Code vanished for a moment (#82).
/// </summary>
public static unsafe class ZOrder
{
    /// <summary>Guards the walks against a z-order that changes under them.</summary>
    private const int MaxSteps = 10_000;

    /// <summary>
    /// Called with each <c>WM_WINDOWPOSCHANGING</c> of <paramref name="self"/>. A move to the front while
    /// <paramref name="upper"/> already is the front window with <paramref name="self"/> right below it keeps the order
    /// instead, so nothing comes between them and <paramref name="upper"/> stays on top (<see cref="FrontMove.Held"/>).
    /// From behind other windows (another app was active last) the move goes through, and the caller raises
    /// <paramref name="upper"/> as soon as it is done (<see cref="FrontMove.Lifted"/>). Owning the docked window would keep
    /// it above the shell in every case, but a cross-process owner attaches the two programs' input queues: a hung
    /// VS Code would freeze the shell's input.
    /// </summary>
    /// <param name="windowPos">The message's <c>WINDOWPOS</c>.</param>
    public static FrontMove KeepUnder(nint windowPos, nint self, nint upper)
    {
        var pos = (WINDOWPOS*)windowPos;
        if (pos is null || upper == 0 || !IsMoveToFront(pos->flags, pos->hwndInsertAfter))
        {
            return FrontMove.None;
        }

        if (!IsFrontPair(upper, self))
        {
            return FrontMove.Lifted;
        }

        pos->flags |= SET_WINDOW_POS_FLAGS.SWP_NOZORDER;
        return FrontMove.Held;
    }

    internal static bool IsMoveToFront(SET_WINDOW_POS_FLAGS flags, HWND insertAfter) =>
        (flags & SET_WINDOW_POS_FLAGS.SWP_NOZORDER) == 0 && insertAfter == HWND.HWND_TOP;

    /// <summary>
    /// True when <paramref name="upper"/> is the front window below the always-on-top ones and <paramref name="lower"/>
    /// sits right below it. Windows nobody sees (hidden, cloaked) do not count.
    /// </summary>
    public static bool IsFrontPair(nint upper, nint lower) => IsFrontPair(upper, lower, Above, Seen, IsTopmost);

    internal static bool IsFrontPair(nint upper, nint lower, Func<nint, nint> above, Func<nint, bool> seen, Func<nint, bool> topmost)
    {
        if (upper == 0 || lower == 0 || upper == lower || NextSeen(lower) != upper)
        {
            return false;
        }

        var steps = 0;
        for (var window = NextSeen(upper); window != 0; window = NextSeen(window))
        {
            if (!topmost(window) || ++steps > MaxSteps)
            {
                return false;
            }
        }

        return true;

        nint NextSeen(nint window)
        {
            for (var i = 0; i < MaxSteps; i++)
            {
                window = above(window);
                if (window == 0 || seen(window))
                {
                    return window;
                }
            }

            return 0;
        }
    }

    private static nint Above(nint hwnd) => PInvoke.GetWindow(new HWND(hwnd), GET_WINDOW_CMD.GW_HWNDPREV);

    private static bool Seen(nint hwnd)
    {
        var h = new HWND(hwnd);
        if (!PInvoke.IsWindowVisible(h))
        {
            return false;
        }

        uint cloaked = 0;
        return PInvoke.DwmGetWindowAttribute(h, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint)).Failed || cloaked == 0;
    }

    private static bool IsTopmost(nint hwnd) =>
        ((WINDOW_EX_STYLE)PInvoke.GetWindowLong(new HWND(hwnd), WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE) & WINDOW_EX_STYLE.WS_EX_TOPMOST) != 0;

    /// <summary>Whether the mouse is over the window's client area: its content, not its title bar or frame.</summary>
    public static bool CursorInClientArea(nint hwnd)
    {
        var h = new HWND(hwnd);
        if (!PInvoke.GetCursorPos(out var point) || !PInvoke.ScreenToClient(h, ref point) || !PInvoke.GetClientRect(h, out var client))
        {
            return false;
        }

        return point.X >= client.left && point.X < client.right && point.Y >= client.top && point.Y < client.bottom;
    }
}

/// <summary>What <see cref="ZOrder.KeepUnder"/> made of a window's move.</summary>
public enum FrontMove
{
    /// <summary>No move to the front, or no window to keep above.</summary>
    None,

    /// <summary>The move to the front was held back: the window stays right below the other one.</summary>
    Held,

    /// <summary>The window goes over the other one: raise that one again once the move is done.</summary>
    Lifted,
}
