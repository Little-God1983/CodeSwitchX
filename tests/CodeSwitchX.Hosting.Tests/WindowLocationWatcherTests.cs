using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.Tests;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Hosting.Tests;

public class WindowLocationWatcherTests
{
    [Fact]
    public void A_WinEvent_hook_that_fails_is_logged_so_a_silent_lack_of_snap_back_can_be_found()
    {
        var log = new ListLogger<WindowLocationWatcher>();

        using var watcher = new WindowLocationWatcher(log, setHook: _ => default);

        watcher.IsHooked.ShouldBeFalse();
        log.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("snap"), "nothing else says that dragged windows will not be put back");
    }

    [Fact]
    public void The_hook_is_set_on_this_machine()
    {
        var log = new ListLogger<WindowLocationWatcher>();

        using var watcher = new WindowLocationWatcher(log);

        watcher.IsHooked.ShouldBeTrue();
        log.Entries.ShouldBeEmpty();
    }
}
