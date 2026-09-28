using System.Windows;
using System.Windows.Controls;
using CodeSwitchX.UI.Shell;
using H.NotifyIcon;

namespace CodeSwitchX.UI.Infrastructure;

public sealed class TrayIconService
{
    private TaskbarIcon? _icon;

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
        menu.Items.Add(MenuItem("Exit", () => Application.Current.Shutdown()));

        // H.NotifyIcon's IconSource only accepts URI-backed bitmaps; the app icon is a vector DrawingImage, so build the GDI icon ourselves.
        _icon = new TaskbarIcon
        {
            ToolTipText = "CodeSwitchX",
            Icon = IconRenderer.ToIcon(window.Icon, 32),
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
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
