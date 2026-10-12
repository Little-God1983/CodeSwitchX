using System.Windows;
using System.Windows.Controls;

namespace CodeSwitchX.UI.Yard;

/// <summary>
/// A workspace file, solution or folder dropped on the Yard opens Add workspace with it (the Background is Transparent
/// so the empty board between the tiles takes the drop too).
/// </summary>
public partial class YardView : UserControl
{
    private IDataObject? _dragData;
    private string? _dragPath;

    public YardView()
    {
        InitializeComponent();
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

    private void OnTileMenuClosing(object sender, ContextMenuEventArgs e) => (DataContext as YardViewModel)?.MenuOpen(false);

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
