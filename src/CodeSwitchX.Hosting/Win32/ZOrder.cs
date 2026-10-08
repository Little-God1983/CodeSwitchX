using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CodeSwitchX.Hosting.Win32;

/// <summary>
/// Keeps the shell under the VS Code window docked in its Cab. Activating a window brings it to the front: a click on
/// the shell put the shell over VS Code until VS Code was raised again, so VS Code vanished for a moment (#82).
/// <para>
/// VS Code cannot be raised over the shell once the shell has the foreground: Windows takes a move of another program's
/// window to the top, asynchronous or not, and leaves it where it was (checked on screen with VS Code and with a plain
/// window, #213). So the shell is kept under VS Code, or puts itself back under it; it never asks VS Code to rise.
/// </para>
/// </summary>
public static unsafe class ZOrder
{
    /// <summary>Guards the walks against a z-order that changes under them.</summary>
    private const int MaxSteps = 10_000;

    /// <summary>
    /// Called with each <c>WM_WINDOWPOSCHANGING</c> of <paramref name="self"/>. A move over <paramref name="upper"/>
    /// (<see cref="IsMoveOver"/>) while <paramref name="upper"/> already is the front window with <paramref name="self"/>
    /// right below it keeps the order instead, so nothing comes between them and <paramref name="upper"/> stays on top
    /// (<see cref="FrontMove.Held"/>).
    /// From behind a window that covers it (another app was active last) the move goes through, and the caller tucks
    /// <paramref name="self"/> back under <paramref name="upper"/> as soon as it is done (<see cref="FrontMove.Lifted"/>,
    /// <see cref="TuckUnder"/>). Owning the docked window would keep it above the shell in every case, but a cross-process
    /// owner attaches the two programs' input queues: a hung VS Code would freeze the shell's input.
    /// </summary>
    /// <param name="windowPos">The message's <c>WINDOWPOS</c>.</param>
    public static FrontMove KeepUnder(nint windowPos, nint self, nint upper)
    {
        var pos = (WINDOWPOS*)windowPos;
        if (pos is null || upper == 0 || !IsMoveOver(pos->flags, pos->hwndInsertAfter, upper, self, Above, Owner))
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

    /// <summary>
    /// Whether a move may put <paramref name="self"/> over <paramref name="upper"/>: to the top, right under a window that
    /// is over <paramref name="upper"/>, or right under a window <paramref name="self"/> owns. A click on the shell comes as
    /// the last kind: Windows takes the shell's hidden IME window, which it keeps over the shell, to the top in the same
    /// move and puts the shell right under it, so where that window stands before the move tells nothing (#213). Held
    /// back while the two are the front pair anyway, such a move changes nothing that matters.
    /// </summary>
    /// <remarks>One walk up from <paramref name="upper"/>, at most <see cref="MaxSteps"/> windows.</remarks>
    internal static bool IsMoveOver(SET_WINDOW_POS_FLAGS flags, HWND insertAfter, nint upper, nint self, Func<nint, nint> above,
        Func<nint, nint> owner)
    {
        if ((flags & SET_WINDOW_POS_FLAGS.SWP_NOZORDER) != 0)
        {
            return false;
        }

        if (insertAfter == HWND.HWND_TOP)
        {
            return true;
        }

        // HWND_BOTTOM, HWND_TOPMOST and HWND_NOTOPMOST are no windows to look for.
        var after = (nint)insertAfter.Value;
        if (after is 1 or -1 or -2)
        {
            return false;
        }

        if (owner(after) == self)
        {
            return true;
        }

        var window = upper;
        for (var steps = 0; steps < MaxSteps; steps++)
        {
            window = above(window);
            if (window == 0)
            {
                return false;
            }

            if (window == after)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="upper"/> is the front window below the always-on-top ones and <paramref name="lower"/>
    /// sits right below it, as far as <paramref name="lower"/> can tell: only windows that cover part of it count. Windows
    /// nobody sees (hidden, cloaked) do not count either, nor do windows on another monitor: the browser the user had last
    /// there made every click on the shell bury VS Code (#213).
    /// </summary>
    public static bool IsFrontPair(nint upper, nint lower)
    {
        var frame = Frame(lower);
        return IsFrontPair(upper, lower, Above, Seen, IsTopmost, w => frame is { } f && Frame(w) is { } r && Overlap(r, f));
    }

    /// <remarks>
    /// One walk up from <paramref name="lower"/>, at most <see cref="MaxSteps"/> windows: a walk that runs out (a z-order
    /// changing under it, more hidden windows than that) proves nothing and answers false, as does any window seen between
    /// that covers <paramref name="lower"/>.
    /// </remarks>
    internal static bool IsFrontPair(nint upper, nint lower, Func<nint, nint> above, Func<nint, bool> seen, Func<nint, bool> topmost,
        Func<nint, bool> covers)
    {
        if (upper == 0 || lower == 0 || upper == lower)
        {
            return false;
        }

        var passedUpper = false;
        var window = lower;
        for (var steps = 0; steps < MaxSteps; steps++)
        {
            window = above(window);
            if (window == 0)
            {
                return passedUpper; // the top of the z-order
            }

            if (!seen(window) || (window != upper && !covers(window)))
            {
                continue;
            }

            if (!passedUpper)
            {
                if (window != upper)
                {
                    return false; // a window between them
                }

                passedUpper = true;
            }
            else if (!topmost(window))
            {
                return false; // a window over the pair
            }
        }

        return false;
    }

    /// <summary>
    /// Puts <paramref name="self"/> right under <paramref name="upper"/> after it went over it (<see cref="FrontMove.Lifted"/>),
    /// and lowers under <paramref name="self"/> every window of another program that covers it from above
    /// <paramref name="upper"/>: VS Code and the shell end up in front together, as if VS Code had been raised. Windows
    /// refuses that raise but lets a window be lowered. The lowering is asynchronous, so a hung program does not hold the
    /// shell.
    /// </summary>
    public static void TuckUnder(nint self, nint upper)
    {
        if (upper == 0 || self == upper)
        {
            return;
        }

        var frame = Frame(self);
        var covering = Covering(upper, self, Above, Seen, IsTopmost, w => frame is { } f && Frame(w) is { } r && Overlap(r, f), IsOurs);
        var keep = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
        PInvoke.SetWindowPos(new HWND(self), new HWND(upper), 0, 0, 0, 0, keep);
        // Nearest VS Code first, each right under the shell: the ones higher up land above them, in their old order.
        foreach (var window in covering)
        {
            PInvoke.SetWindowPos(new HWND(window), new HWND(self), 0, 0, 0, 0, keep | SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS);
        }
    }

    /// <summary>
    /// The windows of other programs above <paramref name="upper"/> that anybody sees, that cover <paramref name="self"/>
    /// and do not stay on top of everything: the ones <see cref="TuckUnder"/> lowers, nearest <paramref name="upper"/> first.
    /// The shell's own windows are left alone: those it owns go where it goes.
    /// </summary>
    /// <remarks>One walk up from <paramref name="upper"/>, at most <see cref="MaxSteps"/> windows.</remarks>
    internal static IReadOnlyList<nint> Covering(nint upper, nint self, Func<nint, nint> above, Func<nint, bool> seen,
        Func<nint, bool> topmost, Func<nint, bool> covers, Func<nint, bool> ours)
    {
        var covering = new List<nint>();
        var window = upper;
        for (var steps = 0; steps < MaxSteps; steps++)
        {
            window = above(window);
            if (window == 0)
            {
                break;
            }

            if (window != self && seen(window) && !topmost(window) && covers(window) && !ours(window) && !covering.Contains(window))
            {
                covering.Add(window);
            }
        }

        return covering;
    }

    /// <summary>Whether two rectangles share any pixel; ones that only touch, or are of no size, do not.</summary>
    internal static bool Overlap(ScreenRect a, ScreenRect b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom && a.Width > 0 && a.Height > 0 && b.Width > 0 && b.Height > 0;

    /// <summary>
    /// What the window covers on screen: DWM's frame bounds, without the invisible resize borders a window rectangle has
    /// (a maximized window's reach 8 px onto the next monitor). The window rectangle where DWM says nothing.
    /// </summary>
    private static ScreenRect? Frame(nint hwnd)
    {
        var h = new HWND(hwnd);
        RECT rect;
        if (PInvoke.DwmGetWindowAttribute(h, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &rect, (uint)sizeof(RECT)).Failed
            && !PInvoke.GetWindowRect(h, out rect))
        {
            return null;
        }

        return new ScreenRect(rect.left, rect.top, rect.right, rect.bottom);
    }

    private static bool IsOurs(nint hwnd)
    {
        uint process;
        return PInvoke.GetWindowThreadProcessId(new HWND(hwnd), &process) != 0 && process == (uint)Environment.ProcessId;
    }

    private static nint Above(nint hwnd) => PInvoke.GetWindow(new HWND(hwnd), GET_WINDOW_CMD.GW_HWNDPREV);

    private static nint Owner(nint hwnd) => PInvoke.GetWindow(new HWND(hwnd), GET_WINDOW_CMD.GW_OWNER);

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
