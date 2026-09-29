using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.Tests;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace CodeSwitchX.Hosting.Tests;

public class WindowLocationWatcherTests
{
    private const uint SomeOtherEvent = 0x0003; // EVENT_SYSTEM_FOREGROUND

    [Fact]
    public void A_WinEvent_hook_that_fails_is_logged_so_a_silent_lack_of_snap_back_can_be_found()
    {
        var log = new ListLogger<WindowLocationWatcher>();

        using var watcher = new WindowLocationWatcher(log, setHook: (_, _) => default, unhook: _ => { });

        watcher.IsHooked.ShouldBeFalse();
        log.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("snap"), "nothing else says that moved windows will not be put back");
    }

    [Fact]
    public void The_start_of_a_drag_is_hooked_on_its_own_and_its_failure_is_logged()
    {
        // The move loop can only be ended once it has started, which LOCATIONCHANGE does not tell: it needs its own hook.
        var log = new ListLogger<WindowLocationWatcher>();
        var hooked = new List<uint>();
        var unhooked = new List<HWINEVENTHOOK>();

        var watcher = new WindowLocationWatcher(log, setHook: (eventId, _) =>
        {
            hooked.Add(eventId);
            return eventId == PInvoke.EVENT_OBJECT_LOCATIONCHANGE ? new HWINEVENTHOOK(1) : default;
        }, unhook: unhooked.Add);

        hooked.ShouldBe([PInvoke.EVENT_OBJECT_LOCATIONCHANGE, PInvoke.EVENT_SYSTEM_MOVESIZESTART, PInvoke.EVENT_OBJECT_DESTROY, PInvoke.EVENT_OBJECT_CREATE, PInvoke.EVENT_OBJECT_SHOW]);
        watcher.IsHooked.ShouldBeTrue();
        log.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("drag") && e.Message.Contains("snap-back"),
            "nothing else says that drags are not refused and fight the snap-back");

        watcher.Dispose();
        unhooked.ShouldBe([new HWINEVENTHOOK(1)], "only the hook that was set is unhooked");
    }

    [Fact]
    public void Each_event_raises_its_own_notification_for_the_window_it_names()
    {
        WINEVENTPROC? callback = null;
        using var watcher = new WindowLocationWatcher(null, setHook: (_, cb) => { callback = cb; return new HWINEVENTHOOK(1); }, unhook: _ => { });
        var moved = new List<nint>();
        var started = new List<nint>();
        var destroyed = new List<nint>();
        watcher.Moved += moved.Add;
        watcher.MoveSizeStarted += started.Add;
        watcher.Destroyed += destroyed.Add;
        var appeared = new List<nint>();
        watcher.Appeared += appeared.Add;

        callback!(new HWINEVENTHOOK(1), PInvoke.EVENT_OBJECT_LOCATIONCHANGE, new HWND(500), 0, 0, 0, 0);
        callback(new HWINEVENTHOOK(1), PInvoke.EVENT_SYSTEM_MOVESIZESTART, new HWND(600), 0, 0, 0, 0);
        callback(new HWINEVENTHOOK(1), PInvoke.EVENT_OBJECT_DESTROY, new HWND(650), 0, 0, 0, 0);
        callback(new HWINEVENTHOOK(1), PInvoke.EVENT_OBJECT_DESTROY, new HWND(660), idObject: 0, idChild: 3, 0, 0);
        callback(new HWINEVENTHOOK(1), PInvoke.EVENT_OBJECT_CREATE, new HWND(670), 0, 0, 0, 0);
        callback(new HWINEVENTHOOK(1), PInvoke.EVENT_OBJECT_SHOW, new HWND(670), 0, 0, 0, 0);
        callback(new HWINEVENTHOOK(1), SomeOtherEvent, new HWND(700), 0, 0, 0, 0);
        callback(new HWINEVENTHOOK(1), PInvoke.EVENT_OBJECT_LOCATIONCHANGE, new HWND(800), idObject: -4 /* OBJID_CARET */, 0, 0, 0);
        callback(new HWINEVENTHOOK(1), PInvoke.EVENT_OBJECT_LOCATIONCHANGE, HWND.Null, 0, 0, 0, 0);

        moved.ShouldBe([500]);
        started.ShouldBe([600]);
        destroyed.ShouldBe([650], "an element inside a window is not the window");
        appeared.ShouldBe([670, 670], "created and shown are both reported");
    }
}
