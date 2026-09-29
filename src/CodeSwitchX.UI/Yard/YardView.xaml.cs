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

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedPath(e.Data) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var path = DroppedPath(e.Data);
        _dragData = null;
        if (path is not null && DataContext is YardViewModel yard)
        {
            // Explorer waits in its drag loop until Drop returns; a dialog shown from here would hang it until closed.
            Dispatcher.BeginInvoke(() => yard.AddWorkspaceFrom(path));
        }
    }

    /// <summary>
    /// The path the drop would add, or null. DragOver repeats while the mouse is held still, so the answer, which looks
    /// at the disk, is kept for the drag it was worked out for.
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
