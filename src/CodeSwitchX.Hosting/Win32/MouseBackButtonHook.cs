using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CodeSwitchX.Hosting.Win32;

internal enum BackButtonAction
{
    Pass,
    GoBack,
    Swallow,
}

/// <summary>
/// Takes the mouse back button (XButton1) pressed over the VS Code window the Cab shows, which the shell never sees:
/// the click goes to VS Code, a window of another process. A low-level mouse hook sees every mouse event of the desktop
/// and holds each one until it returns, so it runs on a thread of its own that does nothing else: on the WPF thread, a
/// busy shell would stall the mouse everywhere.
/// </summary>
public sealed class MouseBackButtonHook : IDisposable
{
    private const uint XButton1 = 0x0001;

    private readonly Func<nint, bool> _isShownInCab;
    private readonly Action _backPressed;
    private readonly ILogger? _logger;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private HOOKPROC? _proc;
    private uint _threadId;

    /// <summary>Whether the press being taken still owes its release; touched on the hook's thread only.</summary>
    private bool _releasePending;

    /// <param name="isShownInCab">Whether the top-level window is the one the Cab shows; called on the hook's thread, and must be quick.</param>
    /// <param name="backPressed">Called on the hook's thread for a press that was taken; must hand the work on, not do it.</param>
    public MouseBackButtonHook(Func<nint, bool> isShownInCab, Action backPressed, ILogger? logger = null)
    {
        _isShownInCab = isShownInCab;
        _backPressed = backPressed;
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = "CodeSwitchX back button hook" };
        _thread.Start();
        _started.Wait();
    }

    /// <summary>
    /// A press of the back button over the window the Cab shows goes back, and its release is taken with it wherever the
    /// cursor is by then, so VS Code gets neither half. Anything else goes on to where it was going; only back button
    /// events ask where the cursor is, since every mouse move of the desktop passes through here.
    /// </summary>
    internal static BackButtonAction Decide(uint message, uint mouseData, Func<bool> overShownWindow, ref bool releasePending)
    {
        if (message is not (PInvoke.WM_XBUTTONDOWN or PInvoke.WM_XBUTTONUP) || mouseData >> 16 != XButton1)
        {
            return BackButtonAction.Pass;
        }

        if (message == PInvoke.WM_XBUTTONUP)
        {
            var pending = releasePending;
            releasePending = false;
            return pending ? BackButtonAction.Swallow : BackButtonAction.Pass;
        }

        releasePending = overShownWindow();
        return releasePending ? BackButtonAction.GoBack : BackButtonAction.Pass;
    }

    private void Run()
    {
        _threadId = PInvoke.GetCurrentThreadId();
        _proc = OnMouse;
        using var module = PInvoke.GetModuleHandle((string?)null);
        using var hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, _proc, module, 0);
        _started.Set();
        if (hook.IsInvalid)
        {
            // Nothing else would say why the back button does nothing over VS Code.
            _logger?.LogWarning("SetWindowsHookEx was refused; the mouse back button does not return to the Yard while VS Code is under the cursor");
            return;
        }

        // The hook is called from inside GetMessage; the loop ends with the WM_QUIT that Dispose posts.
        while (PInvoke.GetMessage(out _, HWND.Null, 0, 0) > 0)
        {
        }
    }

    private unsafe LRESULT OnMouse(int code, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (code >= 0)
            {
                var info = (MSLLHOOKSTRUCT*)lParam.Value;
                var point = info->pt;
                var action = Decide((uint)wParam.Value, info->mouseData, () => _isShownInCab(RootWindowAt(point)), ref _releasePending);
                if (action == BackButtonAction.GoBack)
                {
                    _backPressed();
                }

                if (action != BackButtonAction.Pass)
                {
                    return new LRESULT(1);
                }
            }
        }
        catch (Exception ex)
        {
            // An exception on this thread would end the process, and the event must go on either way.
            _logger?.LogWarning(ex, "The mouse back button hook failed on an event; it was passed on");
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
    }

    private static unsafe nint RootWindowAt(System.Drawing.Point point)
    {
        var window = PInvoke.WindowFromPoint(point);
        return window == HWND.Null ? 0 : (nint)PInvoke.GetAncestor(window, GET_ANCESTOR_FLAGS.GA_ROOT).Value;
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, default, default);
        }

        _thread.Join(TimeSpan.FromSeconds(1));
        _started.Dispose();
    }
}
