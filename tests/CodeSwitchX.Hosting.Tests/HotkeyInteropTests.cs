using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.Hosting.Tests;

public sealed class HotkeyInteropTests
{
    // Only what shows the foreground above this process makes push-to-talk latch: its token says elevated, or the
    // process opened but its token was denied. A process that cannot be opened at all (protected, anti-cheat, another
    // user's) does not hide the keyboard, and neither does anything unknown: those use the release poll.
    [Theory]
    [InlineData(ForegroundElevation.Elevated, false, true)]
    [InlineData(ForegroundElevation.Elevated, null, true)]
    [InlineData(ForegroundElevation.TokenDenied, false, true)]
    [InlineData(ForegroundElevation.TokenDenied, null, true)]
    [InlineData(ForegroundElevation.ProcessDenied, false, false)] // a protected or anti-cheat process
    [InlineData(ForegroundElevation.ProcessDenied, null, false)]
    [InlineData(ForegroundElevation.NotElevated, false, false)]
    [InlineData(ForegroundElevation.Unknown, false, false)]
    [InlineData(ForegroundElevation.Unknown, null, false)]
    [InlineData(ForegroundElevation.Elevated, true, false)] // this process is elevated too: nothing is hidden from it
    [InlineData(ForegroundElevation.TokenDenied, true, false)]
    public void Only_a_foreground_seen_to_be_elevated_above_this_process_hides_the_keys(ForegroundElevation foreground, bool? self, bool expected)
    {
        HotkeyInterop.HidesKeysFromUs(foreground, self).ShouldBe(expected);
    }
}
