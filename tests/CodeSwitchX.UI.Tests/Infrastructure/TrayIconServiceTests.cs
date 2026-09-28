using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using CodeSwitchX.UI.Infrastructure;

namespace CodeSwitchX.UI.Tests.Infrastructure;

public class TrayIconServiceTests
{
    [Fact]
    public async Task The_tray_icon_leaves_CodeSwitchX_at_normal_priority_so_the_vs_code_it_starts_is_too()
    {
        var shell = new ShellTestHarness().Shell;
        var before = CurrentPriority();
        try
        {
            await StaThread.RunAsync(() =>
            {
                var icon = new DrawingImage(new GeometryDrawing(Brushes.Orange, null, new RectangleGeometry(new Rect(0, 0, 32, 32))));
                var window = new Window { Icon = icon, ShowInTaskbar = false, ShowActivated = false };
                var tray = new TrayIconService();
                tray.Attach(window, shell);
                tray.Detach();
            });

            CurrentPriority().ShouldBe(before,
                "idle priority starves the Event API while a build runs, and every VS Code started from CodeSwitchX inherits it");
        }
        finally
        {
            // Put back only what was changed: efficiency mode lowers the priority and throttles the process.
            if (CurrentPriority() != before && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
            {
                H.NotifyIcon.EfficiencyMode.EfficiencyModeUtilities.SetEfficiencyMode(false);
                using var process = Process.GetCurrentProcess();
                process.PriorityClass = before;
            }
        }
    }

    private static ProcessPriorityClass CurrentPriority()
    {
        using var process = Process.GetCurrentProcess();
        return process.PriorityClass;
    }
}
