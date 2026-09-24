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
    ScreenRect? GetRect(nint hwnd);
}
