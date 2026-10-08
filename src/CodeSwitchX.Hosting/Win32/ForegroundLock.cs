using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CodeSwitchX.Hosting.Win32;

/// <summary>
/// Brings a window of this process to the front past Windows' foreground lock. A process that got no input lately may not
/// take the foreground: a voice command to Raven is no input to CodeSwitchX, so "bring CodeSwitchX to the front" left the
/// window in front where it was (#222). For the moment of the switch, this thread shares the input state of the window in
/// front, which lets it take the foreground; no key press is made up.
/// </summary>
public static unsafe class ForegroundLock
{
    /// <summary>How long the window in front may take to answer before it is taken for busy (IsHungAppWindow waits 5 s).</summary>
    private const uint Answer = 200;

    /// <summary>Whether the window's thread pumps its messages: a WM_NULL comes back within <see cref="Answer"/> ms.</summary>
    private static bool Answers(HWND window)
    {
        nuint result;
        // Not SMTO_BLOCK: the shell keeps answering what is sent to it meanwhile, VS Code in its Cab included.
        return PInvoke.SendMessageTimeout(window, PInvoke.WM_NULL, 0, 0,
            SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG | SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_NORMAL, Answer, &result) != 0;
    }

    /// <summary>Whether <paramref name="hwnd"/> has the foreground afterwards.</summary>
    /// <remarks>A window in front that does not answer within <see cref="Answer"/> is left alone: attached to its input, the
    /// activation would wait on it, and a busy or hung app would freeze the shell. One that hangs right after the probe still
    /// can, for as long as it hangs.</remarks>
    public static bool Take(nint hwnd)
    {
        var window = new HWND(hwnd);
        var front = PInvoke.GetForegroundWindow();
        if (front == window)
        {
            return true;
        }

        var frontThread = front.IsNull ? 0 : PInvoke.GetWindowThreadProcessId(front, null);
        var ownThread = PInvoke.GetCurrentThreadId();
        if (frontThread == 0 || frontThread == ownThread || PInvoke.IsHungAppWindow(front) || !Answers(front))
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
