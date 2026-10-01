using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using CodeSwitchX.UI.Shell;
using H.NotifyIcon;

namespace CodeSwitchX.UI.Infrastructure;

public sealed class TrayIconService
{
    private TaskbarIcon? _icon;
    private System.Drawing.Icon? _image;

    public void Attach(Window window, ShellViewModel shell)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Show CodeSwitchX", () => WindowActivation.BringUp(window)));
        menu.Items.Add(MenuItem("Back to Yard", () =>
        {
            shell.BackToYard();
            WindowActivation.BringUp(window);
        }));
        menu.Items.Add(new Separator());
        // Through the window, not a shutdown: closing it asks first while a chat Raven started is working.
        menu.Items.Add(MenuItem("Exit", window.Close));

        _image = LoadAppIcon(TrayIconSize());
        _icon = new TaskbarIcon
        {
            ToolTipText = "CodeSwitchX",
            Icon = _image,
            ContextMenu = menu,
        };
        _icon.TrayLeftMouseDown += (_, _) => WindowActivation.BringUp(window);
        // Not the library's default: its efficiency mode runs the whole process at idle priority, where a build that keeps
        // the CPU busy can starve the Event API, and a VS Code that CodeSwitchX starts inherits that priority.
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void Detach()
    {
        _icon?.Dispose();
        _icon = null;
        // The TaskbarIcon does not own the GDI icon it shows.
        _image?.Dispose();
        _image = null;
    }

    /// <summary>
    /// The application icon (ApplicationIcon, a Win32 resource of this assembly and of the exe) at <paramref name="size"/>
    /// pixels: the frame of that size where the .ico has one, so the tray gets a drawn 16 px S at 100 % and not a scaled 32.
    /// </summary>
    internal static System.Drawing.Icon LoadAppIcon(int size)
    {
        var assembly = typeof(TrayIconService).Assembly.Location;
        var file = assembly.Length > 0 ? assembly : Environment.ProcessPath ?? "";
        return System.Drawing.Icon.ExtractIcon(file, 0, size)
            ?? throw new InvalidOperationException($"{file} carries no application icon.");
    }

    /// <summary>The notification area's icon size: the small icon at the system DPI, 16 px at 100 %, 24 px at 150 %.</summary>
    internal static int TrayIconSize() => GetSystemMetricsForDpi(SmCxSmIcon, GetDpiForSystem());

    private const int SmCxSmIcon = 49;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
