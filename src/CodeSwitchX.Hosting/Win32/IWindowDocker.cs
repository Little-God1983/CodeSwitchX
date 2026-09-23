namespace CodeSwitchX.Hosting.Win32;

public interface IWindowDocker
{
    void MoveTo(nint hwnd, ScreenRect rect);
    void Cloak(nint hwnd);
    void Uncloak(nint hwnd);
    void BringToFront(nint hwnd);
    bool IsAlive(nint hwnd);
    ScreenRect? GetRect(nint hwnd);
}
