using System.Windows;
using System.Windows.Controls;

namespace CodeSwitchX.UI.Yard;

/// <summary>
/// A workspace file, solution or folder dropped on the Yard opens Add workspace with it (the Background is Transparent
/// so the empty board between the tiles takes the drop too).
/// </summary>
public partial class YardView : UserControl
{
    /// <summary>Marks the items <see cref="OnTileMenuOpened"/> put in, so the next opening takes them out first.</summary>
    private static readonly object StableBuildItem = new();

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
    /// A tile's menu opens (#423): its Build new version / Run entries go in above Unregister, made afresh, so a branch
    /// switched or a shortcut written since the last opening counts. In code, because the list changes beside fixed items.
    /// </summary>
    private void OnTileMenuOpened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        foreach (var old in menu.Items.OfType<Control>().Where(i => i.Tag == StableBuildItem).ToList())
        {
            menu.Items.Remove(old);
        }

        if (menu.DataContext is not WorkspaceTileViewModel tile || tile.StableBuildEntries() is not { Count: > 0 } entries)
        {
            return;
        }

        // Above the separator that sets Unregister apart.
        var at = menu.Items.IndexOf(menu.Items.OfType<Separator>().Last());
        menu.Items.Insert(at++, new Separator { Tag = StableBuildItem });
        foreach (var entry in entries)
        {
            menu.Items.Insert(at++, Item(entry.BuildHeader, entry.BuildCommand, entry.BuildBlock));
            menu.Items.Insert(at++, Item(entry.RunHeader, entry.RunCommand, entry.RunBlock));
        }

        static MenuItem Item(string header, System.Windows.Input.ICommand command, string? block)
        {
            var item = new MenuItem { Header = header, Command = command, IsEnabled = block is null, ToolTip = block, Tag = StableBuildItem };
            ToolTipService.SetShowOnDisabled(item, true);
            return item;
        }
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
