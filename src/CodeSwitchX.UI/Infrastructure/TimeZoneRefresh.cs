namespace CodeSwitchX.UI.Infrastructure;

/// <summary>
/// .NET caches the local time zone for the life of the process. Windows tells every top-level window about a new time
/// or time zone with WM_TIMECHANGE (a laptop that sets its zone automatically, a trip); without clearing the cache,
/// "Today" in the performance bar kept turning over at the old zone's midnight until the next start.
/// </summary>
internal static class TimeZoneRefresh
{
    private const int WmTimeChange = 0x001E;

    /// <summary>An HwndSource hook for the shell's window.</summary>
    public static nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmTimeChange)
        {
            TimeZoneInfo.ClearCachedData();
        }

        return 0;
    }
}
