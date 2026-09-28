namespace CodeSwitchX.Hosting.Win32;

public interface IWindowDocker
{
    void MoveTo(nint hwnd, ScreenRect rect);
    /// <summary>Hide the window from the desktop, taskbar and Alt+Tab.</summary>
    void Cloak(nint hwnd);

    /// <summary>Show a hidden window again without activating it.</summary>
    void Uncloak(nint hwnd);
    void BringToFront(nint hwnd);
    bool IsAlive(nint hwnd);

    /// <summary>
    /// True when Windows refuses to move the window: a process that is not elevated may not move, show or hide the
    /// windows of an elevated one (UIPI).
    /// </summary>
    bool IsOutOfReach(nint hwnd);

    /// <summary>True while the user holds the primary mouse button, as in a drag.</summary>
    bool IsPrimaryButtonDown();
    ScreenRect? GetRect(nint hwnd);
}
