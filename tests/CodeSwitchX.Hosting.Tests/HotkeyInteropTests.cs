using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.Hosting.Tests;

public sealed class HotkeyInteropTests
{
    // What shows the foreground above this process makes push-to-talk latch: its token says elevated, the process
    // opened but its token was denied, or it could not be opened at all (an admin window elevated under another
    // account). Anything unknown uses the release poll.
    [Theory]
    [InlineData(ForegroundElevation.Elevated, false, true)]
    [InlineData(ForegroundElevation.Elevated, null, true)]
    [InlineData(ForegroundElevation.TokenDenied, false, true)]
    [InlineData(ForegroundElevation.TokenDenied, null, true)]
    [InlineData(ForegroundElevation.ProcessDenied, false, true)] // another account's admin window
    [InlineData(ForegroundElevation.ProcessDenied, null, true)]
    [InlineData(ForegroundElevation.NotElevated, false, false)]
    [InlineData(ForegroundElevation.Unknown, false, false)]
    [InlineData(ForegroundElevation.Unknown, null, false)]
    [InlineData(ForegroundElevation.Elevated, true, false)] // this process is elevated too: nothing is hidden from it
    [InlineData(ForegroundElevation.TokenDenied, true, false)]
    [InlineData(ForegroundElevation.ProcessDenied, true, false)]
    public void Only_a_foreground_seen_to_be_elevated_above_this_process_hides_the_keys(ForegroundElevation foreground, bool? self, bool expected)
    {
        HotkeyInterop.HidesKeysFromUs(foreground, self).ShouldBe(expected);
    }
}
