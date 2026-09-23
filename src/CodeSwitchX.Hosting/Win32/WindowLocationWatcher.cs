using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace CodeSwitchX.Hosting.Win32;

/// <summary>
/// Raises <see cref="Moved"/> when any top-level window moves or resizes. Create and dispose on a thread
/// with a message loop (the WPF UI thread): out-of-context WinEvent callbacks arrive through that loop.
/// </summary>
public sealed class WindowLocationWatcher : IDisposable
{
    private const int ObjIdWindow = 0;
    private readonly WINEVENTPROC _callback;
    private readonly HWINEVENTHOOK _hook;

    public WindowLocationWatcher()
    {
        _callback = OnWinEvent;
        _hook = PInvoke.SetWinEventHook(PInvoke.EVENT_OBJECT_LOCATIONCHANGE, PInvoke.EVENT_OBJECT_LOCATIONCHANGE,
            HMODULE.Null, _callback, 0, 0, PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
    }

    public event Action<nint>? Moved;

    private unsafe void OnWinEvent(HWINEVENTHOOK hook, uint eventId, HWND hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (idObject == ObjIdWindow && idChild == 0 && hwnd != HWND.Null)
        {
            Moved?.Invoke((nint)hwnd.Value);
        }
    }

    public void Dispose()
    {
        if (_hook != default)
        {
            PInvoke.UnhookWinEvent(_hook);
        }
    }
}
