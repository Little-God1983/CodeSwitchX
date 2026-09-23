namespace CodeSwitchX.Hosting.Win32;

public sealed record WindowInfo(nint Hwnd, uint ProcessId, string ClassName, string Title);
