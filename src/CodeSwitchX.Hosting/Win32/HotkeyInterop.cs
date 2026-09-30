using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace CodeSwitchX.Hosting.Win32;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
    NoRepeat = 0x4000,
}

public static class HotkeyInterop
{
    public const int WmHotkey = 0x0312;

    public static bool Register(nint hwnd, int id, HotkeyModifiers modifiers, uint virtualKey) =>
        PInvoke.RegisterHotKey(new HWND(hwnd), id, (HOT_KEY_MODIFIERS)(uint)modifiers, virtualKey);

    public static bool Unregister(nint hwnd, int id) => PInvoke.UnregisterHotKey(new HWND(hwnd), id);

    /// <summary>Whether the key is physically down right now, whichever window has the keyboard: a hotkey's release reaches no window.</summary>
    public static bool IsKeyDown(uint virtualKey) => (PInvoke.GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;

    /// <summary>
    /// How long ago the message being handled on this thread was posted (GetMessageTime, on the same millisecond clock
    /// as <see cref="Environment.TickCount"/>, whose wrap the unchecked difference absorbs). Only meaningful inside the
    /// handling of a posted message such as WM_HOTKEY.
    /// </summary>
    public static TimeSpan CurrentMessageAge() => TimeSpan.FromMilliseconds(unchecked(Environment.TickCount - PInvoke.GetMessageTime()));

    /// <summary>
    /// Whether the foreground window belongs to an elevated process while this one is not: Windows then hides the
    /// keyboard from GetAsyncKeyState here (every key reads as up). A token that cannot even be opened for a query is
    /// taken as elevated, which is what denies it; any other failure as not elevated.
    /// </summary>
    public static unsafe bool IsForegroundElevated()
    {
        var hwnd = PInvoke.GetForegroundWindow();
        if (hwnd.IsNull)
        {
            return false;
        }

        uint pid;
        PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId)
        {
            return false;
        }

        return ProcessElevation(pid) != false && ProcessElevation((uint)Environment.ProcessId) != true;
    }

    private const int ErrorAccessDenied = 5;

    /// <summary>True or false from the process token; null when it could not be read for another reason than access.</summary>
    private static unsafe bool? ProcessElevation(uint pid)
    {
        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process.IsNull)
        {
            return Marshal.GetLastPInvokeError() == ErrorAccessDenied ? true : null;
        }

        try
        {
            HANDLE token;
            if (!PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, &token))
            {
                return Marshal.GetLastPInvokeError() == ErrorAccessDenied ? true : null;
            }

            try
            {
                TOKEN_ELEVATION elevation;
                uint size;
                if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenElevation, &elevation, (uint)sizeof(TOKEN_ELEVATION), &size))
                {
                    return null;
                }

                return elevation.TokenIsElevated != 0;
            }
            finally
            {
                PInvoke.CloseHandle(token);
            }
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }
}
