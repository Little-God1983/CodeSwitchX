using System.Runtime.InteropServices;
using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.Hosting.Tests;

public class SnapWindowDockerTests
{
    [Fact]
    public void Uncloak_shows_a_window_whose_hide_is_still_waiting_in_its_queue()
    {
        // Cloak posts the hide to the window's own thread. Until a busy VS Code gets to it, the window still counts as
        // visible, and a release that trusted that would let the hide land after CodeSwitchX has gone.
        using var created = new ManualResetEventSlim();
        using var pump = new ManualResetEventSlim();
        nint hwnd = 0;
        var visibleAfterPump = false;
        var owner = new Thread(() =>
        {
            hwnd = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "STATIC", "CodeSwitchX uncloak probe", WS_POPUP | WS_VISIBLE,
                -20000, -20000, 10, 10, 0, 0, 0, 0);
            created.Set();
            pump.Wait();
            while (PeekMessageW(out var msg, 0, 0, 0, PM_REMOVE))
            {
                TranslateMessage(msg);
                DispatchMessageW(msg);
            }

            visibleAfterPump = IsWindowVisible(hwnd);
            DestroyWindow(hwnd);
        });
        owner.Start();
        created.Wait(TestContext.Current.CancellationToken);
        hwnd.ShouldNotBe(nint.Zero);
        var docker = new SnapWindowDocker();

        docker.Cloak(hwnd);
        IsWindowVisible(hwnd).ShouldBeTrue("the hide is still queued");
        docker.Uncloak(hwnd);
        pump.Set();
        owner.Join();

        visibleAfterPump.ShouldBeTrue();
    }

    [Fact]
    public void A_window_of_the_same_elevation_is_within_reach_and_a_closed_one_is_not_reported_as_out_of_it()
    {
        var hwnd = CreateWindowExW(0, "STATIC", "CodeSwitchX reach probe", 0, 0, 0, 10, 10, 0, 0, 0, 0);
        hwnd.ShouldNotBe(nint.Zero);
        var docker = new SnapWindowDocker();

        docker.IsOutOfReach(hwnd).ShouldBeFalse();
        DestroyWindow(hwnd);
        docker.IsOutOfReach(hwnd).ShouldBeFalse("a closed window is the liveness poll's business");
    }

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint PM_REMOVE = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out Msg msg, nint hwnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in Msg msg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(in Msg msg);
}
