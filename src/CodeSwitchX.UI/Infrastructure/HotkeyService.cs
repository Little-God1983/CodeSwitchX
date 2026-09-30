using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.UI.Shell;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Infrastructure;

public sealed record HotkeyBinding(int Id, HotkeyModifiers Modifiers, uint VirtualKey, string Label)
{
    /// <summary>The chord as the user presses it, such as "Ctrl+Shift+Space".</summary>
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
/// Ctrl+Alt+Y toggles Yard/Cab; Ctrl+Shift+Alt+1..9 jumps to a tile. The digit row deliberately adds Shift:
/// AltGr is reported to RegisterHotKey as Ctrl+Alt, so a bare Ctrl+Alt+digit hotkey would swallow AltGr+2/3/7/8/9/0
/// (² ³ { [ ] }) in every application on German and many other European layouts while CodeSwitchX runs.
/// Ctrl+Shift+Space is Raven's push-to-talk and Ctrl+Alt+J folds its panel; neither brings the shell up, so talking
/// works while VS Code keeps the focus.
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
    private const uint VkControl = 0x11;

    /// <summary>How often a held push-to-talk chord is checked for its release, which no window message reports.</summary>
    private static readonly TimeSpan ReleasePoll = TimeSpan.FromMilliseconds(30);

    private readonly ILogger<HotkeyService> _logger;
    private HwndSource? _source;
    private ShellViewModel? _shell;
    private nint _hwnd;
    private DispatcherTimer? _talkRelease;

    public HotkeyService(ILogger<HotkeyService> logger)
    {
        _logger = logger;
    }

    public static IReadOnlyList<HotkeyBinding> Bindings { get; } = BuildBindings();

    /// <summary>The bindings the last <see cref="Attach"/> could not register: another application holds them.</summary>
    public IReadOnlyList<HotkeyBinding> FailedBindings { get; private set; } = [];

    /// <summary>Push-to-talk and the panel toggle, whose loss the Raven panel explains.</summary>
    public static bool IsRavenBinding(HotkeyBinding binding) => binding.Id is PushToTalkId or ToggleRavenId;

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

        // NoRepeat on push-to-talk: while the chord is held, autorepeat must not press the mic again (the panel resets its
        // gesture when a recording reaches its length limit and counts on no repeat arriving after that).
        bindings.Add(new(PushToTalkId, HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.NoRepeat, VkSpace, "Push to talk"));
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
            var index = id - JumpBaseId;
            var acts = index <= _shell.Yard.Tiles.Count();
            _ = _shell.JumpToAsync(index);
            BringUpShellIf(acts);
            handled = true;
        }
        else if (id == PushToTalkId)
        {
            _shell.Raven.PressMic();
            WatchForTalkRelease();
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
    /// A hotkey reports its press only. The chord counts as released once Space or Ctrl is up; a quick tap is released
    /// within a poll or two, which the panel's gesture reads as a tap that latches the mic on.
    /// </summary>
    private void WatchForTalkRelease()
    {
        if (_talkRelease is null)
        {
            _talkRelease = new DispatcherTimer(DispatcherPriority.Input) { Interval = ReleasePoll };
            _talkRelease.Tick += (_, _) =>
            {
                if (HotkeyInterop.IsKeyDown(VkSpace) && HotkeyInterop.IsKeyDown(VkControl))
                {
                    return;
                }

                _talkRelease.Stop();
                if (_shell is not null)
                {
                    _ = _shell.Raven.ReleaseMicAsync();
                }
            };
        }

        _talkRelease.Stop();
        _talkRelease.Start();
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
