using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.Tests;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.UI.Accessibility;

namespace CodeSwitchX.Hosting.Tests;

public class WindowLocationWatcherTests
{
    [Fact]
    public void A_WinEvent_hook_that_fails_is_logged_so_a_silent_lack_of_snap_back_can_be_found()
    {
        var log = new ListLogger<WindowLocationWatcher>();

        using var watcher = new WindowLocationWatcher(log, setHook: (_, _) => default);

        watcher.IsHooked.ShouldBeFalse();
        log.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("snap"), "nothing else says that dragged windows will not be put back");
    }

    [Fact]
    public void The_start_of_a_drag_is_hooked_on_its_own_and_its_failure_is_logged()
    {
        // The move loop can only be ended once it has started, which LOCATIONCHANGE does not tell: it needs its own hook.
        var log = new ListLogger<WindowLocationWatcher>();
        var hooked = new List<uint>();

        using var watcher = new WindowLocationWatcher(log, setHook: (eventId, _) =>
        {
            hooked.Add(eventId);
            return eventId == PInvoke.EVENT_OBJECT_LOCATIONCHANGE ? new HWINEVENTHOOK(1) : default;
        });

        hooked.ShouldBe([PInvoke.EVENT_OBJECT_LOCATIONCHANGE, PInvoke.EVENT_SYSTEM_MOVESIZESTART]);
        watcher.IsHooked.ShouldBeTrue();
        log.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("drag"), "nothing else says that a docked window can be dragged away");
    }
}
