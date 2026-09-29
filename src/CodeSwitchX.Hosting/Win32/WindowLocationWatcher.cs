using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace CodeSwitchX.Hosting.Win32;

/// <summary>
/// Raises <see cref="Moved"/> when any top-level window moves or resizes, and <see cref="MoveSizeStarted"/> when the
/// user starts to drag or resize one by its frame, and <see cref="Destroyed"/> when one is destroyed. Create and dispose on a thread with a message loop (the WPF UI
/// thread): out-of-context WinEvent callbacks arrive through that loop.
/// </summary>
public sealed class WindowLocationWatcher : IDisposable
{
    private const int ObjIdWindow = 0;
    private readonly WINEVENTPROC _callback;
    private readonly Action<HWINEVENTHOOK> _unhook;
    private readonly HWINEVENTHOOK _locationHook;
    private readonly HWINEVENTHOOK _moveSizeHook;
    private readonly HWINEVENTHOOK _destroyHook;

    public WindowLocationWatcher(ILogger<WindowLocationWatcher>? logger = null)
        : this(logger,
            (eventId, callback) => PInvoke.SetWinEventHook(eventId, eventId, HMODULE.Null, callback, 0, 0, PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS),
            hook => PInvoke.UnhookWinEvent(hook))
    {
    }

    internal WindowLocationWatcher(ILogger? logger, Func<uint, WINEVENTPROC, HWINEVENTHOOK> setHook, Action<HWINEVENTHOOK> unhook)
    {
        _callback = OnWinEvent;
        _unhook = unhook;
        _locationHook = setHook(PInvoke.EVENT_OBJECT_LOCATIONCHANGE, _callback);
        if (_locationHook == default)
        {
            // Nothing else would say why a window moved out of the Cab is never put back. No error code: SetWinEventHook
            // sets none the runtime keeps, so a code read here could be another call's.
            logger?.LogWarning("SetWinEventHook was refused; VS Code windows moved out of the Cab are not snapped back");
        }

        // Its own hook: the move loop can only be ended once it has started, and LOCATIONCHANGE does not tell that.
        _moveSizeHook = setHook(PInvoke.EVENT_SYSTEM_MOVESIZESTART, _callback);
        if (_moveSizeHook == default)
        {
            logger?.LogWarning("SetWinEventHook was refused; drags of a docked VS Code window are not refused and fight the snap-back");
        }

        _destroyHook = setHook(PInvoke.EVENT_OBJECT_DESTROY, _callback);
        if (_destroyHook == default)
        {
            logger?.LogWarning("SetWinEventHook was refused; a closed VS Code window is noticed only by the liveness poll");
        }
    }

    /// <summary>False when Windows refused the location hook: <see cref="Moved"/> never fires then.</summary>
    public bool IsHooked => _locationHook != default;

    public event Action<nint>? Moved;

    /// <summary>The user has pressed a window's frame to drag or resize it; Windows' move loop has begun and moved nothing yet.</summary>
    public event Action<nint>? MoveSizeStarted;

    /// <summary>
    /// A window is being destroyed. Windows sends this while the destruction is still under way, when IsWindow may still
    /// say yes: take the event at its word and do not ask IsWindow.
    /// </summary>
    public event Action<nint>? Destroyed;

    private unsafe void OnWinEvent(HWINEVENTHOOK hook, uint eventId, HWND hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (idObject != ObjIdWindow || idChild != 0 || hwnd == HWND.Null)
        {
            return;
        }

        if (eventId == PInvoke.EVENT_SYSTEM_MOVESIZESTART)
        {
            MoveSizeStarted?.Invoke((nint)hwnd.Value);
        }
        else if (eventId == PInvoke.EVENT_OBJECT_LOCATIONCHANGE)
        {
            Moved?.Invoke((nint)hwnd.Value);
        }
        else if (eventId == PInvoke.EVENT_OBJECT_DESTROY)
        {
            Destroyed?.Invoke((nint)hwnd.Value);
        }
    }

    public void Dispose()
    {
        if (_locationHook != default)
        {
            _unhook(_locationHook);
        }

        if (_moveSizeHook != default)
        {
            _unhook(_moveSizeHook);
        }

        if (_destroyHook != default)
        {
            _unhook(_destroyHook);
        }
    }
}
