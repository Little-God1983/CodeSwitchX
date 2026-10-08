using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CodeSwitchX.Hosting.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CodeSwitchX.Hosting.Tests;

public sealed class ZOrderTests
{
    private const nint VsCode = 10;
    private const nint Shell = 20;

    /// <summary>Windows front to back: a handle, whether anybody sees it, whether it stays on top. Each one covers the shell.</summary>
    private static bool IsFrontPair(nint upper, nint lower, params (nint Hwnd, bool Seen, bool Topmost)[] stack) =>
        IsFrontPairOver(upper, lower, [.. stack.Select(w => (w.Hwnd, w.Seen, w.Topmost, Covers: true))]);

    /// <summary>Windows front to back, and whether each one covers part of the shell.</summary>
    private static bool IsFrontPairOver(nint upper, nint lower, params (nint Hwnd, bool Seen, bool Topmost, bool Covers)[] stack) =>
        ZOrder.IsFrontPair(upper, lower, AboveIn(stack), h => stack.Single(w => w.Hwnd == h).Seen, h => stack.Single(w => w.Hwnd == h).Topmost,
            h => stack.Single(w => w.Hwnd == h).Covers);

    /// <summary>Windows front to back: a handle, whether anybody sees it, whether it stays on top, covers the shell, is the shell's process's.</summary>
    private static IReadOnlyList<nint> Covering(params (nint Hwnd, bool Seen, bool Topmost, bool Covers, bool Ours)[] stack) =>
        ZOrder.Covering(VsCode, Shell, AboveIn(stack), h => stack.Single(w => w.Hwnd == h).Seen, h => stack.Single(w => w.Hwnd == h).Topmost,
            h => stack.Single(w => w.Hwnd == h).Covers, h => stack.Single(w => w.Hwnd == h).Ours);

    private static Func<nint, nint> AboveIn<T>(T[] stack) where T : ITuple => hwnd =>
    {
        var i = Array.FindIndex(stack, w => (nint)w[0]! == hwnd);
        return i > 0 ? (nint)stack[i - 1][0]! : 0;
    };

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
    public void A_window_on_the_other_monitor_over_the_pair_does_not_break_it()
    {
        // The browser the user had last on the other monitor (#213): a click on the shell must still keep it under VS Code.
        IsFrontPairOver(VsCode, Shell, (1, true, false, false), (VsCode, true, false, true), (Shell, true, false, true)).ShouldBeTrue();
    }

    [Fact]
    public void A_window_between_them_that_does_not_touch_the_shell_does_not_break_the_pair()
    {
        IsFrontPairOver(VsCode, Shell, (VsCode, true, false, true), (5, true, false, false), (Shell, true, false, true)).ShouldBeTrue();
    }

    [Fact]
    public void A_window_that_covers_the_shell_still_breaks_the_pair()
    {
        IsFrontPairOver(VsCode, Shell, (1, true, false, false), (2, true, false, true), (VsCode, true, false, true), (Shell, true, false, true))
            .ShouldBeFalse();
    }

    [Fact]
    public void A_walk_that_runs_out_of_steps_proves_no_pair()
    {
        // The z-order changes under the walk and a hidden window keeps coming back above itself.
        const nint hidden = 99;
        ZOrder.IsFrontPair(VsCode, Shell, h => h == Shell ? VsCode : hidden, h => h != hidden, _ => false, _ => true).ShouldBeFalse();
    }

    [Fact]
    public void The_windows_to_lower_are_other_apps_over_VS_Code_that_cover_the_shell_nearest_VS_Code_first()
    {
        // The shell has just gone to the front over VS Code.
        Covering(
                (1, true, true, true, false), // the taskbar
                (Shell, true, false, true, true),
                (2, true, false, true, false), // Explorer over the Raven panel
                (3, true, false, false, false), // a browser on the other monitor
                (4, false, false, true, false), // a hidden window
                (5, true, false, true, true), // a window of the shell's own
                (6, true, false, true, false), // another app over the Cab
                (VsCode, true, false, true, false),
                (7, true, false, true, false)) // below VS Code already
            .ShouldBe([6, 2]);
    }

    [Fact]
    public void Nothing_is_lowered_when_nothing_covers_the_shell_over_VS_Code()
    {
        Covering((Shell, true, false, true, true), (VsCode, true, false, true, false), (7, true, false, true, false)).ShouldBeEmpty();
    }

    [Fact]
    public void A_lowering_walk_that_runs_out_of_steps_ends()
    {
        const nint hidden = 99;
        ZOrder.Covering(VsCode, Shell, _ => hidden, h => h != hidden, _ => false, _ => true, _ => false).ShouldBeEmpty();
    }

    [Fact]
    public void Frames_that_only_touch_do_not_overlap()
    {
        // A browser maximized on the monitor left of the shell's ends where the shell's begins.
        var shell = new ScreenRect(0, 0, 3440, 1380);

        ZOrder.Overlap(new ScreenRect(-2400, 0, 0, 1350), shell).ShouldBeFalse();
        ZOrder.Overlap(new ScreenRect(-40000, -40000, -39751, -39957), shell).ShouldBeFalse("parked off screen");
        ZOrder.Overlap(new ScreenRect(100, 100, 100, 100), shell).ShouldBeFalse("of no size");
        ZOrder.Overlap(new ScreenRect(-100, 500, 1, 600), shell).ShouldBeTrue();
        ZOrder.Overlap(new ScreenRect(100, 100, 200, 200), shell).ShouldBeTrue();
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
