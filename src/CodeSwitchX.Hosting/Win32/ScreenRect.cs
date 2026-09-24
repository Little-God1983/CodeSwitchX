namespace CodeSwitchX.Hosting.Win32;

/// <summary>Physical screen pixels, Win32 convention (right and bottom exclusive).</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public static ScreenRect FromSize(int left, int top, int width, int height) => new(left, top, left + width, top + height);
}
