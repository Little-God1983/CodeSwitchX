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
        if (pos is null || upper == 0 || !IsMoveOver(pos->flags, pos->hwndInsertAfter, upper, self, Above, Owner, Seen))
        {
            return FrontMove.None;
        }

        if (!IsFrontPair(upper, self, Destination(self, pos)))
        {
            return FrontMove.Lifted;
        }

        pos->flags |= SET_WINDOW_POS_FLAGS.SWP_NOZORDER;
        return FrontMove.Held;
    }

    /// <summary>
    /// Where a move that also moves or resizes the window takes it (a restore, a maximize), as a window rectangle; null for
    /// a move in the z-order only. Judged before the move, the shell's frame is still the old one: a window over only its
    /// new part would not count, and the shell would grow under it (#215).
    /// </summary>
    private static ScreenRect? Destination(nint self, WINDOWPOS* pos)
    {
        var keep = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE;
        if ((pos->flags & keep) == keep || !PInvoke.GetWindowRect(new HWND(self), out var now))
        {
            return null;
        }

        return Destination(pos->flags, pos->x, pos->y, pos->cx, pos->cy, new ScreenRect(now.left, now.top, now.right, now.bottom));
    }

    /// <summary>The window rectangle a move gives: its new place, its new size, or both, the rest as <paramref name="now"/>.</summary>
    internal static ScreenRect? Destination(SET_WINDOW_POS_FLAGS flags, int x, int y, int cx, int cy, ScreenRect now)
    {
        var keepPlace = (flags & SET_WINDOW_POS_FLAGS.SWP_NOMOVE) != 0;
        var keepSize = (flags & SET_WINDOW_POS_FLAGS.SWP_NOSIZE) != 0;
        if (keepPlace && keepSize)
        {
            return null;
        }

        var (left, top) = keepPlace ? (now.Left, now.Top) : (x, y);
        var (width, height) = keepSize ? (now.Width, now.Height) : (cx, cy);
        return ScreenRect.FromSize(left, top, width, height);
    }

    /// <summary>What a window covers before and after a move: its frame now and where it goes, both. A window rectangle
    /// reaches a few pixels past the frame, so the pair may count as covered when it is not: that only tucks the shell
    /// under VS Code again, where it already is. A frame nobody can tell stays unknown, so every window covers it.</summary>
    internal static Func<nint, ScreenRect?> Reaching(nint self, ScreenRect? destination, Func<nint, ScreenRect?> frame) =>
        destination is not { } to ? frame : w => w != self ? frame(w) : frame(w) is { } f ? Union(f, to) : null;

    internal static ScreenRect Union(ScreenRect a, ScreenRect b) =>
        new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    /// <summary>
    /// Whether a move may put <paramref name="self"/> over <paramref name="upper"/>: to the top, right under a window that
    /// is over <paramref name="upper"/>, or right under a hidden window <paramref name="self"/> owns. A click on the shell
    /// comes as the last kind: Windows takes the shell's hidden IME window, which it keeps over the shell, to the top in the
    /// same move and puts the shell right under it, so where that window stands before the move tells nothing (#213). Held
    /// back while the two are the front pair anyway, such a move changes nothing that matters. Right under a dialog of its
    /// own the shell follows that dialog to the front: tucked back under VS Code, it would take the dialog along and hide it.
    /// </summary>
    /// <remarks>One walk up from <paramref name="upper"/>, at most <see cref="MaxSteps"/> windows.</remarks>
    internal static bool IsMoveOver(SET_WINDOW_POS_FLAGS flags, HWND insertAfter, nint upper, nint self, Func<nint, nint> above,
        Func<nint, nint> owner, Func<nint, bool> seen)
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
            return !seen(after);
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
    /// sits right below it, as far as <paramref name="lower"/> can tell: only windows that cover part of it count
    /// (<see cref="CoversOf"/>). Windows nobody sees (hidden, cloaked) do not count either, nor do windows on another
    /// monitor: the browser the user had last there made every click on the shell bury VS Code (#213).
    /// </summary>
    public static bool IsFrontPair(nint upper, nint lower) => IsFrontPair(upper, lower, destination: null);

    /// <param name="destination">Where a move under way takes <paramref name="lower"/>: what it covers there counts too.</param>
    private static bool IsFrontPair(nint upper, nint lower, ScreenRect? destination) =>
        IsFrontPair(upper, lower, Above, Seen, IsTopmost, CoversOf(lower, upper, Reaching(lower, destination, Frame), RootOwner));

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

        var covering = Covering(upper, self, Above, Seen, IsTopmost, CoversOf(self, upper, Frame, RootOwner), IsOurs);
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

    /// <summary>
    /// Which windows cover part of <paramref name="self"/>, for <see cref="IsFrontPair(nint, nint)"/> and
    /// <see cref="TuckUnder"/>. A window of <paramref name="upper"/>'s (a dialog VS Code shows) does not: it goes where
    /// VS Code goes, and lowered under the shell it would be hidden. A frame nobody can tell counts as covering.
    /// </summary>
    internal static Func<nint, bool> CoversOf(nint self, nint upper, Func<nint, ScreenRect?> frame, Func<nint, nint> rootOwner)
    {
        var own = frame(self);
        return w => rootOwner(w) != upper && (own is not { } f || frame(w) is not { } r || Overlap(r, f));
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

    private static nint RootOwner(nint hwnd) => PInvoke.GetAncestor(new HWND(hwnd), GET_ANCESTOR_FLAGS.GA_ROOTOWNER);

    /// <summary>Whether <paramref name="hwnd"/> is <paramref name="window"/> or a window it owns: a dialog VS Code shows.</summary>
    public static bool IsOf(nint hwnd, nint window) => hwnd != 0 && window != 0 && (hwnd == window || RootOwner(hwnd) == window);

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
    /// <summary>No move over the other window, or no window to keep above.</summary>
    None,

    /// <summary>The move over the other window was held back: the window stays right below it.</summary>
    Held,

    /// <summary>
    /// The window goes over the other one: put it back under that one once the move is done (<see cref="ZOrder.TuckUnder"/>).
    /// Raising the other one instead does nothing while this one has the foreground (#213).
    /// </summary>
    Lifted,
}
