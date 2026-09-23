using System.Windows;
using Microsoft.Win32;

namespace CodeSwitchX.UI.Workspaces;

public partial class AddWorkspaceWindow : Window
{
    private readonly AddWorkspaceViewModel _viewModel;

    public AddWorkspaceWindow(AddWorkspaceViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Closed += () => Dispatcher.BeginInvoke(Close);
    }

    private void BrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Pick the workspace root folder" };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.InputPath = dialog.FolderName;
            _viewModel.ProbeCommand.Execute(null);
        }
    }

    private void BrowseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Workspace or solution|*.code-workspace;*.sln;*.slnx", Title = "Pick a .code-workspace or solution" };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.InputPath = dialog.FileName;
            _viewModel.ProbeCommand.Execute(null);
        }
    }
}
