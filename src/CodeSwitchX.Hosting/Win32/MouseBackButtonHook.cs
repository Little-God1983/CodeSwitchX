using System.Runtime.InteropServices;
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
/// <para>
/// Even there, every mouse event waits for this process, and a blocking garbage collection or a debugger break in it
/// holds the cursor of the whole desktop; a proc that takes too long is also removed by Windows without a word. So the
/// hook is in place only while the Cab shows a window (<see cref="SetActive"/>), and put in afresh each time.
/// </para>
/// </summary>
public sealed class MouseBackButtonHook : IDisposable
{
    private const uint XButton1 = 0x0001;
    private const uint Install = PInvoke.WM_APP + 1;
    private const uint Remove = PInvoke.WM_APP + 2;
    private const uint RemoveOwed = PInvoke.WM_APP + 3;

    private readonly Func<nint, bool> _isShownInCab;
    private readonly Action _backPressed;
    private readonly ILogger? _logger;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private HOOKPROC? _proc;
    private uint _threadId;

    // Touched on the hook's thread only.
    private UnhookWindowsHookExSafeHandle? _hook;
    private SafeHandle? _module;

    /// <summary>Whether the press being taken still owes its release.</summary>
    private bool _releasePending;

    /// <summary>The hook was asked out while a release was owed; it goes once that release is taken.</summary>
    private bool _removeAfterRelease;

    /// <param name="isShownInCab">Whether the top-level window is the one the Cab shows; called on the hook's thread, and must be quick and take no lock.</param>
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

    /// <summary>Puts the hook in place (afresh, if it was) or takes it out; any thread, and it returns at once.</summary>
    public void SetActive(bool active)
    {
        if (_threadId != 0)
        {
            PInvoke.PostThreadMessage(_threadId, active ? Install : Remove, default, default);
        }
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
        _proc = OnMouse;
        using var module = PInvoke.GetModuleHandle((string?)null);
        _module = module;
        // The first PeekMessage-family call gives the thread its message queue, so no SetActive posted after this is lost.
        PInvoke.PeekMessage(out _, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);
        _threadId = PInvoke.GetCurrentThreadId();
        _started.Set();

        // The hook is called from inside GetMessage; the loop ends with the WM_QUIT that Dispose posts.
        while (PInvoke.GetMessage(out var message, HWND.Null, 0, 0) > 0)
        {
            if (message.message == Install)
            {
                Unhook();
                Hook();
            }
            else if (message.message == Remove)
            {
                if (_releasePending)
                {
                    _removeAfterRelease = true;
                }
                else
                {
                    Unhook();
                }
            }
            else if (message.message == RemoveOwed && _removeAfterRelease)
            {
                // Unless the hook was put in afresh meanwhile.
                Unhook();
            }
        }

        Unhook();
    }

    private void Hook()
    {
        _removeAfterRelease = false;
        _hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, _proc, _module, 0);
        if (_hook.IsInvalid)
        {
            // Nothing else would say why the back button does nothing over VS Code.
            _logger?.LogWarning("SetWindowsHookEx was refused; the mouse back button does not return to the Yard while VS Code is under the cursor");
            Unhook();
        }
    }

    private void Unhook()
    {
        _hook?.Dispose();
        _hook = null;
        _releasePending = false;
        _removeAfterRelease = false;
    }

    private unsafe LRESULT OnMouse(int code, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            // Every other mouse event of the desktop goes on at once, before anything is allocated for it.
            if (code >= 0 && (uint)wParam.Value is PInvoke.WM_XBUTTONDOWN or PInvoke.WM_XBUTTONUP)
            {
                var info = (MSLLHOOKSTRUCT*)lParam.Value;
                var point = info->pt;
                var action = Decide((uint)wParam.Value, info->mouseData, () => _isShownInCab(RootWindowAt(point)), ref _releasePending);
                if (action == BackButtonAction.GoBack)
                {
                    _backPressed();
                }

                if (action == BackButtonAction.Swallow && _removeAfterRelease)
                {
                    // Taken out after this call returns: a hook removed from inside its own proc still finishes the call.
                    PInvoke.PostThreadMessage(_threadId, RemoveOwed, default, default);
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
