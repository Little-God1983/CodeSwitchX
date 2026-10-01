using System.Runtime.InteropServices;
using CodeSwitchX.Hosting.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CodeSwitchX.Hosting.Tests;

public sealed class ZOrderTests
{
    private const nint VsCode = 10;
    private const nint Shell = 20;

    /// <summary>Windows front to back: a handle, whether anybody sees it, whether it stays on top.</summary>
    private static bool IsFrontPair(nint upper, nint lower, params (nint Hwnd, bool Seen, bool Topmost)[] stack)
    {
        nint Above(nint hwnd)
        {
            var i = Array.FindIndex(stack, w => w.Hwnd == hwnd);
            return i > 0 ? stack[i - 1].Hwnd : 0;
        }

        return ZOrder.IsFrontPair(upper, lower, Above, h => stack.Single(w => w.Hwnd == h).Seen, h => stack.Single(w => w.Hwnd == h).Topmost);
    }

    [Fact]
    public void VS_Code_right_above_the_shell_at_the_front_is_the_front_pair()
    {
        IsFrontPair(VsCode, Shell, (VsCode, true, false), (Shell, true, false), (30, true, false)).ShouldBeTrue();
    }

    [Fact]
    public void Windows_on_top_of_everything_and_windows_nobody_sees_do_not_break_the_pair()
    {
        IsFrontPair(VsCode, Shell,
            (1, true, true), // the taskbar
            (2, false, false), // a hidden window
            (VsCode, true, false),
            (3, false, false),
            (Shell, true, false)).ShouldBeTrue();
    }

    [Fact]
    public void A_window_over_the_pair_breaks_it_so_the_activation_brings_the_shell_to_the_front()
    {
        // A game the user switched to sits over both: clicking the shell must still bring it forward.
        IsFrontPair(VsCode, Shell, (1, true, false), (VsCode, true, false), (Shell, true, false)).ShouldBeFalse();
    }

    [Fact]
    public void A_window_between_them_or_the_shell_above_VS_Code_is_no_pair()
    {
        IsFrontPair(VsCode, Shell, (VsCode, true, false), (5, true, false), (Shell, true, false)).ShouldBeFalse();
        IsFrontPair(VsCode, Shell, (Shell, true, false), (VsCode, true, false)).ShouldBeFalse();
    }

    [Fact]
    public void Without_a_VS_Code_window_in_the_Cab_there_is_no_pair()
    {
        IsFrontPair(0, Shell, (Shell, true, false)).ShouldBeFalse();
    }

    [Fact]
    public void Only_a_move_to_the_front_is_held_back()
    {
        var keep = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE;

        ZOrder.IsMoveToFront(keep, HWND.HWND_TOP).ShouldBeTrue();
        ZOrder.IsMoveToFront(keep | SET_WINDOW_POS_FLAGS.SWP_NOZORDER, HWND.HWND_TOP).ShouldBeFalse("a move or resize only");
        ZOrder.IsMoveToFront(keep, new HWND(42)).ShouldBeFalse("placed below a window of its own, a dialog it owns say");
        ZOrder.IsMoveToFront(keep, new HWND(-1)).ShouldBeFalse("made always-on-top");
    }

    [Fact]
    public void Nothing_is_held_back_while_the_Cab_shows_no_VS_Code()
    {
        var pos = Marshal.AllocHGlobal(Marshal.SizeOf<WINDOWPOS>());
        try
        {
            Marshal.StructureToPtr(new WINDOWPOS { flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE, hwndInsertAfter = HWND.HWND_TOP }, pos, false);

            ZOrder.KeepUnder(pos, Shell, upper: 0).ShouldBe(FrontMove.None);

            (Marshal.PtrToStructure<WINDOWPOS>(pos).flags & SET_WINDOW_POS_FLAGS.SWP_NOZORDER).ShouldBe(default);
        }
        finally
        {
            Marshal.FreeHGlobal(pos);
        }
    }
}
