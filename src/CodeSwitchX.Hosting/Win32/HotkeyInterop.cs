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

/// <summary>What reading the foreground process's elevation found.</summary>
public enum ForegroundElevation
{
    NotElevated,

    /// <summary>The process token says elevated.</summary>
    Elevated,

    /// <summary>The process could not be opened at all: another account's (an admin window elevated with typed-in
    /// credentials, which hides the keyboard), or one hardened against access (anti-cheat).</summary>
    ProcessDenied,

    /// <summary>The process opened, but its token was denied: what an elevated process does to one that is not.</summary>
    TokenDenied,

    /// <summary>Any other failure to read it.</summary>
    Unknown,
}

public static class HotkeyInterop
{
    public const int WmHotkey = 0x0312;

    public static bool Register(nint hwnd, int id, HotkeyModifiers modifiers, uint virtualKey) =>
        PInvoke.RegisterHotKey(new HWND(hwnd), id, (HOT_KEY_MODIFIERS)(uint)modifiers, virtualKey);

    public static bool Unregister(nint hwnd, int id) => PInvoke.UnregisterHotKey(new HWND(hwnd), id);

    /// <summary>
    /// Whether the key (or mouse button) is physically down right now, whichever window has the keyboard: a hotkey's
    /// release reaches no window. The one GetAsyncKeyState test in the app. While an elevated window is in front of this
    /// unelevated process, Windows reads every key as up here (UIPI).
    /// </summary>
    public static bool IsKeyDown(uint virtualKey) => (PInvoke.GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;

    /// <summary>
    /// How long ago the message being handled on this thread was posted (GetMessageTime, on the same millisecond clock
    /// as <see cref="Environment.TickCount"/>, whose wrap the unchecked difference absorbs). Only meaningful inside the
    /// handling of a posted message such as WM_HOTKEY.
    /// </summary>
    public static TimeSpan CurrentMessageAge() => TimeSpan.FromMilliseconds(unchecked(Environment.TickCount - PInvoke.GetMessageTime()));

    /// <summary>
    /// Whether the foreground window belongs to an elevated process while this one is not: Windows then hides the
    /// keyboard from GetAsyncKeyState here (every key reads as up). Only for a key that already reads as up: a key that
    /// reads as down proves the keyboard is visible. See <see cref="HidesKeysFromUs"/> for what counts.
    /// </summary>
    public static unsafe bool IsForegroundElevated()
    {
        var hwnd = Win32WindowEnumerator.Foreground();
        if (hwnd == 0)
        {
            return false;
        }

        uint pid;
        PInvoke.GetWindowThreadProcessId(new HWND(hwnd), &pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId)
        {
            return false;
        }

        return HidesKeysFromUs(ProcessElevation(pid), SelfElevated.Value);
    }

    /// <summary>
    /// The decision on the two reads. The foreground hides the keys when it is seen to be elevated (its token says so,
    /// or its process opened and its token was denied) and when its process cannot be opened at all: that is how an
    /// admin window elevated under another account looks. A hardened process (anti-cheat) looks the same without hiding
    /// anything, but it is asked about only for a key that already reads as up, which over such a window is a quick tap
    /// that latches anyway: the press only gains the note. Anything unknown uses the release poll. Never while this process is
    /// elevated itself (<paramref name="selfElevated"/> true; null when it could not be read).
    /// </summary>
    internal static bool HidesKeysFromUs(ForegroundElevation foreground, bool? selfElevated) =>
        foreground is ForegroundElevation.Elevated or ForegroundElevation.TokenDenied or ForegroundElevation.ProcessDenied
        && selfElevated != true;

    private const int ErrorAccessDenied = 5;

    /// <summary>This process's own elevation, read once: it cannot change while the process runs.</summary>
    private static readonly Lazy<bool?> SelfElevated = new(() => ProcessElevation((uint)Environment.ProcessId) switch
    {
        ForegroundElevation.Elevated => true,
        ForegroundElevation.NotElevated => false,
        _ => null,
    });

    private static unsafe ForegroundElevation ProcessElevation(uint pid)
    {
        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process.IsNull)
        {
            return Marshal.GetLastPInvokeError() == ErrorAccessDenied ? ForegroundElevation.ProcessDenied : ForegroundElevation.Unknown;
        }

        try
        {
            HANDLE token;
            if (!PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, &token))
            {
                return Marshal.GetLastPInvokeError() == ErrorAccessDenied ? ForegroundElevation.TokenDenied : ForegroundElevation.Unknown;
            }

            try
            {
                TOKEN_ELEVATION elevation;
                uint size;
                if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenElevation, &elevation, (uint)sizeof(TOKEN_ELEVATION), &size))
                {
                    return ForegroundElevation.Unknown;
                }

                return elevation.TokenIsElevated != 0 ? ForegroundElevation.Elevated : ForegroundElevation.NotElevated;
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
