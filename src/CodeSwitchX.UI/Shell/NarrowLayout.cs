using System.Windows;

namespace CodeSwitchX.UI.Shell;

/// <summary>
/// What folds on a narrow window. In the Cab, Raven's chat list folds to its numbers when VS Code would be short of
/// <see cref="CabNeeds"/> without it. In Settings (#162), first the list, then the Settings sidebar to its icons, each only
/// when the page would be short of <see cref="PageNeeds"/> without it. A wide window folds nothing. The views take their
/// widths from here: RavenPanelView's list and chat, MainWindow's closed panel, SettingsView's sidebar.
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

    /// <summary>The least VS Code works well in, in the Cab: its sidebar and an editor of some 100 columns.</summary>
    public const double CabNeeds = 960;

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

    /// <summary>
    /// Whether Raven's list folds in the Cab for a window this wide: only when VS Code beside the open panel would be short
    /// of <see cref="CabNeeds"/>, which needs content 1517 wide. A maximized 1080p window up to 125 % keeps the list, unless a
    /// taskbar at the side takes its width; a closed panel, or a width not known yet, folds nothing.
    /// </summary>
    public static bool FoldsInCab(double width, bool ravenOpen) =>
        ravenOpen && double.IsFinite(width) && width > 0 && width - PanelEdge - RavenChat - RavenList < CabNeeds;
}
