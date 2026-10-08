namespace CodeSwitchX.Hosting.Win32;

public interface IWindowDocker
{
    void MoveTo(nint hwnd, ScreenRect rect);
    /// <summary>Hide the window from the desktop, taskbar and Alt+Tab.</summary>
    void Cloak(nint hwnd);

    /// <summary>Show a hidden window again without activating it.</summary>
    void Uncloak(nint hwnd);
    void BringToFront(nint hwnd);

    /// <summary>
    /// Puts the window above the others without activating it: the window in the foreground keeps the keyboard and the mouse.
    /// Not above the foreground window, though: Windows leaves the window under that one (#213, see <see cref="ZOrder.TuckUnder"/>).
    /// </summary>
    void PlaceOnTop(nint hwnd);

    /// <summary>
    /// Puts the window right under <paramref name="above"/> without activating it: under a dialog of the shell's, so the
    /// dialog stays in front whenever the asynchronous move lands (#215).
    /// </summary>
    void PlaceUnder(nint hwnd, nint above);

    /// <summary>
    /// Ends the drag or resize the user has started on the window's frame, before it has moved anything, without
    /// waiting for the window's thread. Nothing happens when the window is not in a drag or resize any more.
    /// </summary>
    void CancelMoveSize(nint hwnd);
    bool IsAlive(nint hwnd);

    /// <summary>
    /// True when Windows refuses to move the window: a process that is not elevated may not move, show or hide the
    /// windows of an elevated one (UIPI).
    /// </summary>
    bool IsOutOfReach(nint hwnd);
    ScreenRect? GetRect(nint hwnd);
}
