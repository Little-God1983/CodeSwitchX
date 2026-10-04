using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.UI.Shell;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Infrastructure;

public sealed record HotkeyBinding(int Id, HotkeyModifiers Modifiers, uint VirtualKey, string Label)
{
    /// <summary>The chord as the user presses it, such as "Ctrl+Alt+Space".</summary>
    public string Keys
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(HotkeyModifiers.Control))
            {
                parts.Add("Ctrl");
            }

            if (Modifiers.HasFlag(HotkeyModifiers.Shift))
            {
                parts.Add("Shift");
            }

            if (Modifiers.HasFlag(HotkeyModifiers.Alt))
            {
                parts.Add("Alt");
            }

            if (Modifiers.HasFlag(HotkeyModifiers.Win))
            {
                parts.Add("Win");
            }

            parts.Add(VirtualKey == 0x20 ? "Space" : ((char)VirtualKey).ToString());
            return string.Join("+", parts);
        }
    }
}

/// <summary>
/// Ctrl+Alt+Y toggles Yard/Cab; Ctrl+Shift+Alt+1..9 opens the workspace with that number (Workspace.Number), wherever
/// its tile is. The digit row deliberately adds Shift:
/// AltGr is reported to RegisterHotKey as Ctrl+Alt, so a bare Ctrl+Alt+digit hotkey would swallow AltGr+2/3/7/8/9/0
/// (² ³ { [ ] }) in every application on German and many other European layouts while CodeSwitchX runs.
/// <see cref="PushToTalk"/> is Raven's push-to-talk and Ctrl+Alt+J folds its panel; neither brings the shell up, so
/// talking works while VS Code keeps the focus. A Ctrl+Alt hotkey is fine where AltGr+that key types nothing: Space
/// (AltGr+Space types no character on German and the other common European layouts), J and Y. Push-to-talk is not
/// Ctrl+Shift+Space, which is VS Code's Trigger Parameter Hints in the very window CodeSwitchX docks.
/// </summary>
public sealed class HotkeyService
{
    private const int ToggleId = 1;
    private const int JumpBaseId = 10;
    private const int PushToTalkId = 21;
    private const int ToggleRavenId = 22;
    private const uint VkY = 0x59;
    private const uint Vk1 = 0x31;
    private const uint VkSpace = 0x20;
    private const uint VkJ = 0x4A;
    private const uint VkShift = 0x10;
    private const uint VkControl = 0x11;
    private const uint VkMenu = 0x12;
    private const uint VkLeftWin = 0x5B;
    private const uint VkRightWin = 0x5C;

    /// <summary>How often a held push-to-talk chord is checked for its release, which no window message reports.</summary>
    private static readonly TimeSpan ReleasePoll = TimeSpan.FromMilliseconds(30);

    /// <summary>
    /// The latest a WM_HOTKEY is believed to be. GetMessageTime belongs to the last message this thread retrieved; a
    /// hotkey delivered some other way (sent, not posted) would read an older one. No stall the user sits through is
    /// longer, and a hold longer than this still stops at the first poll either way.
    /// </summary>
    private static readonly TimeSpan MaximumMessageAge = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Raven's push-to-talk chord, the one place it is defined: the registration, the release poll and every text that
    /// names the chord (the panel's caption and tooltip, the admin-window note) follow it. NoRepeat: while the chord is
    /// held, autorepeat must not press the mic again (the panel resets its gesture when a recording reaches its length
    /// limit and counts on no repeat arriving after that).
    /// </summary>
    public static HotkeyBinding PushToTalk { get; } =
        new(PushToTalkId, HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.NoRepeat, VkSpace, "Push to talk");

    public static readonly string UnseenReleaseNote =
        $"Push to talk can't see the key being released while an admin window is in front. Press {PushToTalk.Keys} again to stop.";

    private readonly ILogger<HotkeyService> _logger;
    private readonly Func<uint, bool> _isKeyDown;
    private readonly Func<bool> _isForegroundElevated;
    private readonly Func<TimeSpan> _messageAge;
    private HwndSource? _source;
    private ShellViewModel? _shell;
    private nint _hwnd;
    private DispatcherTimer? _talkRelease;
    private bool _unseenReleaseNoted;

    /// <param name="isKeyDown">Whether a virtual key reads as held; GetAsyncKeyState unless a test replaces it.</param>
    /// <param name="isForegroundElevated">Whether the foreground window is an elevated process's (which hides the
    /// keyboard from GetAsyncKeyState); asked only when the chord's key reads as up. The process token's elevation
    /// unless a test replaces it.</param>
    /// <param name="messageAge">How long ago the WM_HOTKEY being handled was posted; GetMessageTime unless a test
    /// replaces it.</param>
    public HotkeyService(ILogger<HotkeyService> logger, Func<uint, bool>? isKeyDown = null, Func<bool>? isForegroundElevated = null,
        Func<TimeSpan>? messageAge = null)
    {
        _logger = logger;
        _isKeyDown = isKeyDown ?? HotkeyInterop.IsKeyDown;
        _isForegroundElevated = isForegroundElevated ?? HotkeyInterop.IsForegroundElevated;
        _messageAge = messageAge ?? HotkeyInterop.CurrentMessageAge;
    }

    public static IReadOnlyList<HotkeyBinding> Bindings { get; } = BuildBindings();

    /// <summary>The bindings the last <see cref="Attach"/> could not register: another application holds them.</summary>
    public IReadOnlyList<HotkeyBinding> FailedBindings { get; private set; } = [];

    /// <summary>What the Raven panel says when this binding is taken by another app, with the button that does the same
    /// job; null for a binding that is not Raven's.</summary>
    public static string? RavenFailureNote(HotkeyBinding binding)
    {
        var advice = binding.Id switch
        {
            PushToTalkId => "Use the mic button instead.",
            ToggleRavenId => "Use the arrow button instead.",
            _ => null,
        };
        return advice is null ? null : $"{binding.Label} ({binding.Keys}) is taken by another app. {advice}";
    }

    private static HotkeyBinding[] BuildBindings()
    {
        var bindings = new List<HotkeyBinding>
        {
            new(ToggleId, HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.NoRepeat, VkY, "Ctrl+Alt+Y"),
        };
        for (var i = 1; i <= 9; i++)
        {
            bindings.Add(new(JumpBaseId + i, HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt | HotkeyModifiers.NoRepeat,
                Vk1 + (uint)(i - 1), $"Ctrl+Shift+Alt+{i}"));
        }

        bindings.Add(PushToTalk);
        bindings.Add(new(ToggleRavenId, HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.NoRepeat, VkJ, "Collapse or expand Raven"));

        return bindings.ToArray();
    }

    public void Attach(nint hwnd, ShellViewModel shell)
    {
        _hwnd = hwnd;
        _shell = shell;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);

        var failed = new List<HotkeyBinding>();
        foreach (var binding in Bindings)
        {
            if (!HotkeyInterop.Register(_hwnd, binding.Id, binding.Modifiers, binding.VirtualKey))
            {
                _logger.LogWarning("Global hotkey {Hotkey} ({Keys}) is already taken by another application", binding.Label, binding.Keys);
                failed.Add(binding);
            }
        }

        FailedBindings = failed;
    }

    public void Detach()
    {
        if (_hwnd == 0)
        {
            return;
        }

        foreach (var binding in Bindings)
        {
            HotkeyInterop.Unregister(_hwnd, binding.Id);
        }

        _talkRelease?.Stop();
        _source?.RemoveHook(WndProc);
        _source = null;
        _hwnd = 0;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != HotkeyInterop.WmHotkey || _shell is null)
        {
            return 0;
        }

        var id = wParam.ToInt32();
        if (id == ToggleId)
        {
            // ToggleMode's own rule: out of the Cab, or back into the workspace shown last.
            var acts = _shell.Mode == ShellMode.Cab || _shell.ActiveWorkspaceId is not null;
            _shell.ToggleMode();
            BringUpShellIf(acts);
            handled = true;
        }
        else if (id > JumpBaseId && id <= JumpBaseId + 9)
        {
            var number = id - JumpBaseId;
            var acts = _shell.Yard.Tiles.Any(t => t.Number == number);
            _ = _shell.JumpToAsync(number);
            BringUpShellIf(acts);
            handled = true;
        }
        else if (id == PushToTalkId)
        {
            OnPushToTalk(_shell.Raven);
            handled = true;
        }
        else if (id == ToggleRavenId)
        {
            _shell.Raven.TogglePanelCommand.Execute(null);
            handled = true;
        }

        return 0;
    }

    /// <summary>
    /// Decided from what can be seen. A chord key that reads as down proves the keyboard is visible: the release is
    /// polled. Windows hides the keyboard from this process while an elevated window is in front: GetAsyncKeyState then
    /// reads every key as up, and a polled hold would read as released straight away, a tap that latches, recording on
    /// after the user let go. So when the key reads as up and the foreground is seen to be elevated, the press is a tap
    /// on purpose, and the panel says once per session that the next press stops it. A key that reads as up is no proof
    /// on its own: a key the UI thread reads late is up as well.
    /// <para>
    /// Otherwise the release is polled, and the press is timed from when the hotkey message was posted, not from when
    /// the UI thread got to it: a hold handled after a stall, whose key is already up at the first poll, still stops
    /// rather than latching.
    /// </para>
    /// </summary>
    private void OnPushToTalk(RavenPanelViewModel raven)
    {
        if (!_isKeyDown(PushToTalk.VirtualKey) && _isForegroundElevated())
        {
            _talkRelease?.Stop();
            _ = raven.TapMic(TalkInput.Hotkey);
            if (!_unseenReleaseNoted && raven.State == RavenState.Listening)
            {
                _unseenReleaseNoted = true;
                raven.Note(UnseenReleaseNote);
            }

            return;
        }

        var age = _messageAge();
        raven.PressMic(TalkInput.Hotkey, age < TimeSpan.Zero ? TimeSpan.Zero : age > MaximumMessageAge ? MaximumMessageAge : age);
        WatchForTalkRelease();
    }

    /// <summary>
    /// A hotkey reports its press only. The chord counts as released once its key (Space) or any of its modifiers (Ctrl,
    /// Alt) is up; a quick tap is released within a poll or two, which the panel's gesture reads as a tap that latches
    /// the mic on.
    /// </summary>
    private void WatchForTalkRelease()
    {
        if (_talkRelease is null)
        {
            _talkRelease = new DispatcherTimer(DispatcherPriority.Input) { Interval = ReleasePoll };
            _talkRelease.Tick += (_, _) =>
            {
                if (IsPushToTalkHeld())
                {
                    return;
                }

                _talkRelease.Stop();
                if (_shell is not null)
                {
                    _ = _shell.Raven.ReleaseMicAsync(TalkInput.Hotkey);
                }
            };
        }

        _talkRelease.Stop();
        _talkRelease.Start();
    }

    /// <summary>The binding's key and every one of its modifiers are down; modifiers it does not use are not watched.</summary>
    private bool IsPushToTalkHeld()
    {
        var modifiers = PushToTalk.Modifiers;
        return _isKeyDown(PushToTalk.VirtualKey)
            && (!modifiers.HasFlag(HotkeyModifiers.Control) || _isKeyDown(VkControl))
            && (!modifiers.HasFlag(HotkeyModifiers.Alt) || _isKeyDown(VkMenu))
            && (!modifiers.HasFlag(HotkeyModifiers.Shift) || _isKeyDown(VkShift))
            && (!modifiers.HasFlag(HotkeyModifiers.Win) || _isKeyDown(VkLeftWin) || _isKeyDown(VkRightWin));
    }

    /// <summary>
    /// A global hotkey is pressed from anywhere, so a hotkey that switches something brings the shell up: a minimised
    /// shell switched modes out of sight, the Cab showing VS Code on its own and the Yard nothing. It comes up after the
    /// switch, because a restored shell docks the workspace the Cab shows, which must already be the one the hotkey
    /// chose. A hotkey with nothing to do leaves the user's application in front.
    /// </summary>
    private void BringUpShellIf(bool acts)
    {
        if (acts && _source?.RootVisual is Window window)
        {
            WindowActivation.BringUp(window);
        }
    }
}
