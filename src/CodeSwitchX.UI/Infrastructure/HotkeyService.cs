using System.Windows.Interop;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.UI.Shell;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>Ctrl+Alt+Y toggles Yard/Cab; Ctrl+Alt+1..9 jumps to a tile.</summary>
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

    public void Attach(nint hwnd, ShellViewModel shell)
    {
        _hwnd = hwnd;
        _shell = shell;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);

        Register(ToggleId, VkY, "Ctrl+Alt+Y");
        for (var i = 1; i <= 9; i++)
        {
            Register(JumpBaseId + i, Vk1 + (uint)(i - 1), $"Ctrl+Alt+{i}");
        }
    }

    public void Detach()
    {
        if (_hwnd == 0)
        {
            return;
        }

        HotkeyInterop.Unregister(_hwnd, ToggleId);
        for (var i = 1; i <= 9; i++)
        {
            HotkeyInterop.Unregister(_hwnd, JumpBaseId + i);
        }

        _source?.RemoveHook(WndProc);
        _source = null;
        _hwnd = 0;
    }

    private void Register(int id, uint virtualKey, string label)
    {
        if (!HotkeyInterop.Register(_hwnd, id, HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.NoRepeat, virtualKey))
        {
            _logger.LogWarning("Global hotkey {Hotkey} is already taken by another application", label);
        }
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
            _shell.ToggleMode();
            handled = true;
        }
        else if (id > JumpBaseId && id <= JumpBaseId + 9)
        {
            _ = _shell.JumpToAsync(id - JumpBaseId);
            handled = true;
        }

        return 0;
    }
}
