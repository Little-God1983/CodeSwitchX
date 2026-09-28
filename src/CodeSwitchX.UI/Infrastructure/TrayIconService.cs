using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        var appIcon = (ImageSource)Application.Current.FindResource("AppIcon");
        _icon = new TaskbarIcon
        {
            ToolTipText = "CodeSwitchX",
            Icon = IconRenderer.ToIcon(appIcon, 32),
            ContextMenu = menu,
        };
        _icon.TrayLeftMouseDown += (_, _) => WindowActivation.BringUp(window);
        _icon.ForceCreate();
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
