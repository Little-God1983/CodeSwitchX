using System.Diagnostics;
using System.Windows;
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
                var window = new Window { ShowInTaskbar = false, ShowActivated = false };
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

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void The_tray_takes_the_application_icon_at_its_own_size_instead_of_a_scaled_32_px_frame(int size)
    {
        using var icon = TrayIconService.LoadAppIcon(size);

        icon.Width.ShouldBe(size);
        icon.Height.ShouldBe(size);
    }

    [Fact]
    public void The_tray_icon_size_is_the_small_icon_size_of_the_system_dpi()
    {
        TrayIconService.TrayIconSize().ShouldBeInRange(16, 64);
    }

    private static ProcessPriorityClass CurrentPriority()
    {
        using var process = Process.GetCurrentProcess();
        return process.PriorityClass;
    }
}
