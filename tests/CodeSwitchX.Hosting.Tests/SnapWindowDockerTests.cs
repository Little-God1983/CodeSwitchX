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
    public void A_minimized_hidden_window_is_moved_where_it_will_be_restored_and_not_shown_by_the_move()
    {
        // SW_RESTORE on a hidden minimized window showed it at its old restore rectangle, possibly on another monitor, before
        // the move landed. The move sets the restore rectangle instead; the show is Uncloak's, and the next move restores it there.
        using var window = new ProbeWindow(minimizedAndHidden: true);
        var docker = new SnapWindowDocker();
        var rect = ScreenRect.FromSize(10, 20, 300, 200);

        docker.MoveTo(window.Hwnd, rect);
        ProbeWindow.Pump();
        IsWindowVisible(window.Hwnd).ShouldBeFalse("the move must not show a hidden window");
        var placement = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
        GetWindowPlacement(window.Hwnd, ref placement);
        (placement.NormalPosition.Left, placement.NormalPosition.Top).ShouldBe((rect.Left, rect.Top), "the restore rectangle is where the Cab is");

        docker.Uncloak(window.Hwnd);
        ProbeWindow.Pump(); // VS Code gets to the show, as it does before the Cab's next dock
        docker.MoveTo(window.Hwnd, rect);
        ProbeWindow.Pump();

        IsWindowVisible(window.Hwnd).ShouldBeTrue();
        IsIconic(window.Hwnd).ShouldBeFalse();
        GetWindowRect(window.Hwnd, out var shown);
        (shown.Left, shown.Top, shown.Right, shown.Bottom).ShouldBe((rect.Left, rect.Top, rect.Right, rect.Bottom));
    }

    /// <summary>
    /// A window on its own thread, which pumps its messages all the time like a VS Code that is not stalled: a move of a
    /// minimized window sends to that thread synchronously. <see cref="Pump"/> gives the thread time for what was posted.
    /// </summary>
    private sealed class ProbeWindow : IDisposable
    {
        private readonly Thread _owner;
        private readonly System.Collections.Concurrent.ConcurrentBag<uint> _received = [];
        private readonly WndProc _subclass;
        private nint _originalProc;
        private volatile bool _closing;

        public ProbeWindow(bool minimizedAndHidden)
        {
            _subclass = OnMessage;
            using var created = new ManualResetEventSlim();
            _owner = new Thread(() =>
            {
                Hwnd = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "STATIC", "CodeSwitchX placement probe", WS_POPUP | WS_VISIBLE,
                    -20000, -20000, 200, 100, 0, 0, 0, 0);
                _originalProc = SetWindowLongPtrW(Hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_subclass));
                if (minimizedAndHidden)
                {
                    ShowWindow(Hwnd, SW_MINIMIZE);
                    ShowWindow(Hwnd, SW_HIDE);
                }

                created.Set();
                while (!_closing)
                {
                    while (PeekMessageW(out var msg, 0, 0, 0, PM_REMOVE))
                    {
                        TranslateMessage(msg);
                        DispatchMessageW(msg);
                    }

                    Thread.Sleep(1);
                }

                DestroyWindow(Hwnd);
            });
            _owner.Start();
            created.Wait(TestContext.Current.CancellationToken);
            Hwnd.ShouldNotBe(nint.Zero);
        }

        public nint Hwnd { get; private set; }

        /// <summary>True once the window's own thread has seen the message.</summary>
        public bool Received(uint message) => _received.Contains(message);

        private nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
        {
            _received.Add(message);
            return CallWindowProcW(_originalProc, hwnd, message, wParam, lParam);
        }

        /// <summary>Long enough for the posted shows and moves to be processed by the thread above.</summary>
        public static void Pump() => Thread.Sleep(150);

        public void Dispose()
        {
            _closing = true;
            _owner.Join();
        }
    }

    [Fact]
    public void CancelMoveSize_delivers_WM_CANCELMODE_to_the_window_without_waiting_for_it()
    {
        // WM_CANCELMODE ends the move loop DefWindowProc runs while the user drags a window by its frame (checked on
        // screen: a caption drag with it sent at EVENT_SYSTEM_MOVESIZESTART moved the window by nothing). It must go
        // the asynchronous way: a stalled VS Code would otherwise hold the WPF thread, from a WinEvent callback.
        using var window = new ProbeWindow(minimizedAndHidden: false);
        var docker = new SnapWindowDocker();

        docker.CancelMoveSize(window.Hwnd);
        window.Received(WM_CANCELMODE).ShouldBeFalse("the message must not be delivered synchronously on this thread");
        ProbeWindow.Pump();

        window.Received(WM_CANCELMODE).ShouldBeTrue();
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
    private const int SW_HIDE = 0;
    private const int SW_MINIMIZE = 6;
    private const int GWLP_WNDPROC = -4;
    private const uint WM_CANCELMODE = 0x001F;

    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProcW(nint previous, nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public uint Length;
        public uint Flags;
        public uint ShowCmd;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int cmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint hwnd, ref WindowPlacement placement);

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
