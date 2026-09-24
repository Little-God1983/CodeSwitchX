using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace CodeSwitchX.Hosting.Win32;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
    NoRepeat = 0x4000,
}

public static class HotkeyInterop
{
    public const int WmHotkey = 0x0312;

    public static bool Register(nint hwnd, int id, HotkeyModifiers modifiers, uint virtualKey) =>
        PInvoke.RegisterHotKey(new HWND(hwnd), id, (HOT_KEY_MODIFIERS)(uint)modifiers, virtualKey);

    public static bool Unregister(nint hwnd, int id) => PInvoke.UnregisterHotKey(new HWND(hwnd), id);
}
