using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Workspaces;

/// <summary>
/// Opens the Add workspace dialog, one at a time. The dialog is modal, but a drop or a click can arrive while the tracks
/// load, before it is shown.
/// </summary>
public sealed class AddWorkspaceLauncher
{
    private readonly Func<AddWorkspaceViewModel> _factory;
    private readonly Func<AddWorkspaceViewModel, Task> _show;
    private readonly ILogger<AddWorkspaceLauncher> _logger;
    private bool _open;

    /// <param name="show">Shows the dialog for the view model; the task ends when the dialog is closed.</param>
    public AddWorkspaceLauncher(Func<AddWorkspaceViewModel> factory, Func<AddWorkspaceViewModel, Task> show, ILogger<AddWorkspaceLauncher> logger)
    {
        _factory = factory;
        _show = show;
        _logger = logger;
    }

    /// <summary>
    /// Opens the dialog, detecting <paramref name="path"/> at once when one was dropped on the Yard; does nothing while
    /// a dialog is loading or open.
    /// </summary>
    public async Task OpenAsync(string? path)
    {
        if (_open)
        {
            return;
        }

        _open = true;
        try
        {
            var viewModel = _factory();
            await viewModel.LoadAsync(CancellationToken.None);
            if (path is not null)
            {
                viewModel.InputPath = path;
                viewModel.ProbeCommand.Execute(null);
            }

            await _show(viewModel);
        }
        catch (Exception ex)
        {
            // Fire-and-forget from the Yard's button: a failed track load left the dialog unopened without a line anywhere.
            _logger.LogError(ex, "Opening the Add workspace dialog failed");
        }
        finally
        {
            _open = false;
        }
    }
}
