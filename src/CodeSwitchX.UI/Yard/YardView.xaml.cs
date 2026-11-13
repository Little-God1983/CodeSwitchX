using System.Windows;
using System.Windows.Controls;

namespace CodeSwitchX.UI.Yard;

/// <summary>
/// A workspace file, solution or folder dropped on the Yard opens Add workspace with it (the Background is Transparent
/// so the empty board between the tiles takes the drop too).
/// </summary>
public partial class YardView : UserControl
{
    /// <summary>Marks the items <see cref="OnTileContextMenuOpening"/> put in, so the next opening takes them out first.</summary>
    private static readonly object StableBuildItem = new();

    /// <summary>The open tile menu with Build / Run entries and its tile (#425): the entries follow the tile's git rounds until it closes.</summary>
    private (ContextMenu Menu, WorkspaceTileViewModel Tile)? _buildMenu;

    private IDataObject? _dragData;
    private string? _dragPath;

    public YardView()
    {
        InitializeComponent();
        _ = new GitHubPanelFocus(this, GitHubPanel); // lives as long as the two it listens to
    }

    private void OnGitHubShadeClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        (DataContext as YardViewModel)?.GitHubPanel?.CloseCommand.Execute(null);
        e.Handled = true;
    }

    private void OnBoardMouseMove(object sender, System.Windows.Input.MouseEventArgs e) => (DataContext as YardViewModel)?.HoldOrder();

    /// <summary>
    /// The pointer left the board. A context menu that opens takes the mouse, which says it left too while it is still
    /// over the board: only a pointer outside the board lets go of the order.
    /// </summary>
    private void OnBoardMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var board = (FrameworkElement)sender;
        var at = e.GetPosition(board);
        if (at.X < 0 || at.Y < 0 || at.X >= board.ActualWidth || at.Y >= board.ActualHeight)
        {
            (DataContext as YardViewModel)?.ReleaseOrder();
        }
    }

    private void OnTileMenuOpening(object sender, ContextMenuEventArgs e) => (DataContext as YardViewModel)?.MenuOpen(true);

    /// <summary>
    /// A chat row's own menu opens (#386): the row finds out what it offers now. Not handled, so the board hears it too and
    /// holds its order while the menu is open.
    /// </summary>
    private void OnChatMenuOpening(object sender, ContextMenuEventArgs e) => ((sender as FrameworkElement)?.DataContext as ChatRowViewModel)?.MenuOpening();

    private void OnTileMenuClosing(object sender, ContextMenuEventArgs e) => (DataContext as YardViewModel)?.MenuOpen(false);

    /// <summary>
    /// A tile's menu is about to open (#423): its Build new version / Run entries go in above Unregister, made afresh from the
    /// tile's last git round, and a shortcut written since counts at once. Before the menu shows, so it does not grow on screen.
    /// That round can be up to 30 s old (a branch switched a moment ago): the tile's git is read again now, and the entries follow
    /// what it finds while the menu is open (#425). In code, because the list changes beside fixed items. Not handled: the board
    /// holds the tiles' order on it too.
    /// </summary>
    private void OnTileContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu, DataContext: WorkspaceTileViewModel tile } || OpensAMenuOfItsOwn(e.OriginalSource, sender))
        {
            return;
        }

        StopFollowingBuildMenu();
        if (PutStableBuildItems(menu, tile.StableBuildEntries()) && DataContext is YardViewModel yard)
        {
            _buildMenu = (menu, tile);
            tile.PropertyChanged += OnBuildMenuTileChanged;
            menu.Closed += OnBuildMenuClosed;
            yard.RefreshGitOf(tile);
        }
    }

    /// <summary>The tile of the open menu shows a new git round: its entries are made again, if they say anything new.</summary>
    private void OnBuildMenuTileChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceTileViewModel.GitLines) && _buildMenu is var (menu, tile) && menu.IsOpen)
        {
            var entries = tile.StableBuildEntries();
            if (!entries.SelectMany(Look).SequenceEqual(menu.Items.OfType<MenuItem>().Where(i => i.Tag == StableBuildItem).Select(i => (i.Header, (object?)i.ToolTip, i.Command.CanExecute(null)))))
            {
                PutStableBuildItems(menu, entries);
            }
        }

        static IEnumerable<(object Header, object? ToolTip, bool Enabled)> Look(StableBuildEntry entry)
        {
            yield return (Header(entry.BuildHeader), entry.BuildBlock, entry.CanBuild);
            if (entry.RunHeader is { } run)
            {
                yield return (Header(run), entry.RunBlock, entry.CanRun);
            }
        }
    }

    private void OnBuildMenuClosed(object sender, RoutedEventArgs e) => StopFollowingBuildMenu();

    private void StopFollowingBuildMenu()
    {
        if (_buildMenu is var (menu, tile))
        {
            tile.PropertyChanged -= OnBuildMenuTileChanged;
            menu.Closed -= OnBuildMenuClosed;
            _buildMenu = null;
        }
    }

    /// <summary>"__" shows a folder's underscore, which a header would otherwise take for an access key.</summary>
    private static string Header(string text) => text.Replace("_", "__");

    /// <summary>The Build / Run items of <paramref name="entries"/> in place of the ones the menu has; false when there are none.</summary>
    private static bool PutStableBuildItems(ContextMenu menu, IReadOnlyList<StableBuildEntry> entries)
    {
        foreach (var old in menu.Items.OfType<Control>().Where(i => i.Tag == StableBuildItem).ToList())
        {
            menu.Items.Remove(old);
        }

        if (entries.Count == 0)
        {
            return false;
        }

        // Above the separator that sets Unregister apart; at the end should the menu ever have none.
        var at = menu.Items.OfType<Separator>().LastOrDefault() is { } separator ? menu.Items.IndexOf(separator) : menu.Items.Count;
        menu.Items.Insert(at++, new Separator { Tag = StableBuildItem });
        foreach (var entry in entries)
        {
            menu.Items.Insert(at++, Item(entry.BuildHeader, entry.BuildCommand, entry.BuildBlock));
            if (entry.RunHeader is { } run)
            {
                menu.Items.Insert(at++, Item(run, entry.RunCommand, entry.RunBlock));
            }
        }

        return true;

        // Enabled or not by the command's CanExecute; the tooltip says why on a greyed one.
        static MenuItem Item(string header, System.Windows.Input.ICommand command, string? block)
        {
            var item = new MenuItem { Header = Header(header), Command = command, ToolTip = block, Tag = StableBuildItem };
            ToolTipService.SetShowOnDisabled(item, true);
            return item;
        }
    }

    /// <summary>
    /// The right-click was on something inside the tile with a menu of its own (a chat row): that menu opens, not the tile's,
    /// so the tile's entries are not made for nothing. The event still bubbles up through the tile.
    /// </summary>
    private static bool OpensAMenuOfItsOwn(object source, object tile)
    {
        for (var node = source as DependencyObject; node is not null && !ReferenceEquals(node, tile); node = Parent(node))
        {
            if (node is FrameworkElement { ContextMenu: not null })
            {
                return true;
            }
        }

        return false;

        static DependencyObject? Parent(DependencyObject node) =>
            node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedPath(e.Data) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var path = DroppedPath(e.Data);
        ForgetDrag();
        if (path is not null && DataContext is YardViewModel yard)
        {
            // Explorer waits in its drag loop until Drop returns; a dialog shown from here would hang it until closed.
            Dispatcher.BeginInvoke(() => yard.AddWorkspaceFrom(path));
        }
    }

    /// <summary>
    /// A cancelled drag ends here, not in Drop: let go of Explorer's data object. It also fires when the mouse crosses
    /// onto a tile inside the Yard, which only costs working the path out again.
    /// </summary>
    private void OnDragLeave(object sender, DragEventArgs e) => ForgetDrag();

    private void ForgetDrag()
    {
        _dragData = null;
        _dragPath = null;
    }

    /// <summary>
    /// The path the drop would add, or null. DragOver repeats while the mouse is held still, so the answer, read from a
    /// data object that may live in Explorer, is kept for the drag it was worked out for.
    /// </summary>
    private string? DroppedPath(IDataObject data)
    {
        if (!ReferenceEquals(data, _dragData))
        {
            _dragData = data;
            _dragPath = YardViewModel.DroppedWorkspacePath(data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null);
        }

        return _dragPath;
    }
}
