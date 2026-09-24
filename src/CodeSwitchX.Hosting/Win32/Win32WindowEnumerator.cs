using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CodeSwitchX.Hosting.Win32;

public sealed class Win32WindowEnumerator : IWindowEnumerator
{
    public IReadOnlyList<WindowInfo> TopLevelWindows()
    {
        var result = new List<WindowInfo>();
        PInvoke.EnumWindows((hwnd, _) =>
        {
            // Hidden windows are included: a VS Code window hidden by an instance that crashed stays hidden, and
            // the only way to give it back to the user is to find and adopt it again.
            if (PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOTOWNER) != hwnd)
            {
                return true;
            }

            uint pid;
            nint handle;
            unsafe
            {
                PInvoke.GetWindowThreadProcessId(hwnd, &pid);
                handle = (nint)hwnd.Value;
            }

            result.Add(new WindowInfo(handle, pid, ClassNameOf(hwnd), TitleOf(hwnd)) { IsVisible = PInvoke.IsWindowVisible(hwnd) });
            return true;
        }, default);
        return result;
    }

    public string? ProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string ClassNameOf(HWND hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        var length = PInvoke.GetClassName(hwnd, buffer);
        return length > 0 ? new string(buffer[..length]) : string.Empty;
    }

    private static string TitleOf(HWND hwnd)
    {
        var length = PInvoke.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        Span<char> buffer = length < 1024 ? stackalloc char[length + 1] : new char[length + 1];
        var copied = PInvoke.GetWindowText(hwnd, buffer);
        return copied > 0 ? new string(buffer[..copied]) : string.Empty;
    }
}
