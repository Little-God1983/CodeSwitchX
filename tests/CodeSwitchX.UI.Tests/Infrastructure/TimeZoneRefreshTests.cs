using System.Windows.Interop;
using CodeSwitchX.UI.Infrastructure;

namespace CodeSwitchX.UI.Tests.Infrastructure;

public class TimeZoneRefreshTests
{
    private const int WmTimeChange = 0x001E;

    [Fact]
    public async Task The_local_time_zone_is_read_again_after_windows_reports_a_time_change()
    {
        await StaThread.RunAsync(() =>
        {
            using var window = new HwndSource(new HwndSourceParameters("csx-test") { WindowStyle = 0 });
            window.AddHook(TimeZoneRefresh.WndProc);
            var before = TimeZoneInfo.Local;

            StaThread.SendMessage(window.Handle, WmTimeChange, 0, 0);

            // TimeZoneInfo.Local is the same cached object until the cache is cleared.
            TimeZoneInfo.Local.ShouldNotBeSameAs(before, "a zone changed while CodeSwitchX runs must reach the performance bar's Today");
        });
    }
}
