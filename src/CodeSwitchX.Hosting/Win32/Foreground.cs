using Windows.Win32;
using Windows.Win32.Foundation;

namespace CodeSwitchX.Hosting.Win32;

/// <summary>
/// Brings a window of this process to the front past Windows' foreground lock. A process that got no input lately may not
/// take the foreground: a voice command to Raven is no input to CodeSwitchX, so "bring CodeSwitchX to the front" left the
/// window in front where it was (#222). For the moment of the switch, this thread shares the input state of the window in
/// front, which lets it take the foreground; no key press is made up.
/// </summary>
public static unsafe class Foreground
{
    /// <summary>Whether <paramref name="hwnd"/> has the foreground afterwards.</summary>
    public static bool Take(nint hwnd)
    {
        var window = new HWND(hwnd);
        if (PInvoke.GetForegroundWindow() == window || PInvoke.SetForegroundWindow(window) && PInvoke.GetForegroundWindow() == window)
        {
            return true;
        }

        var front = PInvoke.GetForegroundWindow();
        var frontThread = front.IsNull ? 0 : PInvoke.GetWindowThreadProcessId(front, null);
        var ownThread = PInvoke.GetCurrentThreadId();
        if (frontThread == 0 || frontThread == ownThread)
        {
            return false;
        }

        var attached = PInvoke.AttachThreadInput(ownThread, frontThread, true);
        try
        {
            PInvoke.BringWindowToTop(window);
            PInvoke.SetForegroundWindow(window);
        }
        finally
        {
            if (attached)
            {
                PInvoke.AttachThreadInput(ownThread, frontThread, false);
            }
        }

        return PInvoke.GetForegroundWindow() == window;
    }
}
