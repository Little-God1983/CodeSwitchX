using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.Hosting.Tests;

public sealed class HotkeyInteropTests
{
    // Only a known elevated foreground (or one whose token access was denied, which ProcessElevation reports as true)
    // makes push-to-talk latch; an elevation that could not be read uses the release poll like any other window.
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, null, true)]
    [InlineData(false, false, false)]
    [InlineData(null, false, false)]
    [InlineData(null, null, false)]
    [InlineData(true, true, false)] // this process is elevated too: nothing is hidden from it
    public void Only_a_known_elevated_foreground_above_this_process_counts_as_elevated(bool? foreground, bool? self, bool expected)
    {
        HotkeyInterop.HidesKeysFromUs(foreground, self).ShouldBe(expected);
    }
}
