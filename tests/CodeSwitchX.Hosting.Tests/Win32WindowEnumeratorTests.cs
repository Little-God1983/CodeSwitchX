using System.Runtime.InteropServices;
using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.Hosting.Tests;

public class Win32WindowEnumeratorTests
{
    [Fact]
    public void Hidden_top_level_windows_are_enumerated_so_a_window_hidden_by_a_crashed_instance_can_be_found_again()
    {
        var title = "CodeSwitchX hidden probe " + Guid.NewGuid().ToString("N");
        var hwnd = CreateWindowExW(0, "STATIC", title, WS_OVERLAPPED, 0, 0, 10, 10, nint.Zero, nint.Zero, nint.Zero, nint.Zero);
        hwnd.ShouldNotBe(nint.Zero);
        try
        {
            var window = new Win32WindowEnumerator().TopLevelWindows().SingleOrDefault(w => w.Hwnd == hwnd);

            window.ShouldNotBeNull();
            window.Title.ShouldBe(title);
            window.IsVisible.ShouldBeFalse();
        }
        finally
        {
            DestroyWindow(hwnd);
        }
    }

    private const uint WS_OVERLAPPED = 0x00000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);
}
