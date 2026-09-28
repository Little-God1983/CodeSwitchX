using System.Windows;
using System.Windows.Interop;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.UI.Shell;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Infrastructure;

public sealed record HotkeyBinding(int Id, HotkeyModifiers Modifiers, uint VirtualKey, string Label);

/// <summary>
/// Ctrl+Alt+Y toggles Yard/Cab; Ctrl+Shift+Alt+1..9 jumps to a tile. The digit row deliberately adds Shift:
/// AltGr is reported to RegisterHotKey as Ctrl+Alt, so a bare Ctrl+Alt+digit hotkey would swallow AltGr+2/3/7/8/9/0
/// (² ³ { [ ] }) in every application on German and many other European layouts while CodeSwitchX runs.
/// </summary>
public sealed class HotkeyService
{
    private const int ToggleId = 1;
    private const int JumpBaseId = 10;
    private const uint VkY = 0x59;
    private const uint Vk1 = 0x31;

    private readonly ILogger<HotkeyService> _logger;
    private HwndSource? _source;
    private ShellViewModel? _shell;
    private nint _hwnd;

    public HotkeyService(ILogger<HotkeyService> logger)
    {
        _logger = logger;
    }

    public static IReadOnlyList<HotkeyBinding> Bindings { get; } = BuildBindings();

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

        return bindings.ToArray();
    }

    public void Attach(nint hwnd, ShellViewModel shell)
    {
        _hwnd = hwnd;
        _shell = shell;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);

        foreach (var binding in Bindings)
        {
            if (!HotkeyInterop.Register(_hwnd, binding.Id, binding.Modifiers, binding.VirtualKey))
            {
                _logger.LogWarning("Global hotkey {Hotkey} is already taken by another application", binding.Label);
            }
        }
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

        return 0;
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
