namespace CodeSwitchX.Hosting.Win32;

public sealed record WindowInfo(nint Hwnd, uint ProcessId, string ClassName, string Title)
{
    /// <summary>False for a window hidden with SW_HIDE, e.g. by a CodeSwitchX instance that crashed before releasing it.</summary>
    public bool IsVisible { get; init; } = true;
}
