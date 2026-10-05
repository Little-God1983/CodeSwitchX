using System.Windows;

namespace CodeSwitchX.UI.Shell;

/// <summary>
/// What folds on a narrow window so the Settings page keeps room to read (#162): first Raven's chat list to its numbers,
/// as in the Cab, then the Settings sidebar to its icons, each only when the page would be short of
/// <see cref="PageNeeds"/> without it. A wide window folds nothing. The views take their widths from here: RavenPanelView's
/// list and chat, MainWindow's closed panel, SettingsView's sidebar.
/// </summary>
public static class NarrowLayout
{
    /// <summary>Raven's chat, beside its list while the panel is open.</summary>
    public const double RavenChat = 320;

    /// <summary>Raven's chat list, and folded to its numbers; a closed panel is as wide as the folded list.</summary>
    public const double RavenList = 236;

    public const double RavenListFolded = 58;

    /// <summary>The Settings sidebar with the pages' names, and folded to their icons.</summary>
    public const double Sidebar = 260;

    public const double SidebarFolded = 64;

    /// <summary>The least a Settings page reads well in, its margins included.</summary>
    public const double PageNeeds = 480;

    /// <summary>The open panel's 1 px edge beside its chat; a closed panel's is inside its width.</summary>
    public const double PanelEdge = 1;

    public static GridLength RavenChatColumn { get; } = new(RavenChat);

    public static GridLength SidebarColumn { get; } = new(Sidebar);

    public static GridLength SidebarFoldedColumn { get; } = new(SidebarFolded);

    /// <summary>
    /// What folds for a window this wide (its content, in device-independent pixels), with Raven's panel open or not. An
    /// unknown width (not laid out yet) folds nothing.
    /// </summary>
    /// <param name="scrollBar">
    /// The page's scroll bar, counted whether it shows or not: a page that grows to scroll must not lose its room
    /// (<see cref="SystemParameters.VerticalScrollBarWidth"/>).
    /// </param>
    public static (bool RavenList, bool Sidebar) Folds(double width, bool ravenOpen, double scrollBar)
    {
        if (!double.IsFinite(width) || width <= 0)
        {
            return (false, false);
        }

        var page = width - scrollBar - (ravenOpen ? PanelEdge : 0);
        var raven = ravenOpen ? RavenChat + RavenList : RavenListFolded;
        if (page - raven - Sidebar >= PageNeeds)
        {
            return (false, false);
        }

        // A closed panel has no list to fold: only the sidebar can give way.
        var ravenFolded = ravenOpen ? RavenChat + RavenListFolded : RavenListFolded;
        return page - ravenFolded - Sidebar >= PageNeeds ? (ravenOpen, false) : (ravenOpen, true);
    }
}
