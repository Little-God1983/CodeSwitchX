using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodeSwitchX.Core.GitHub;

namespace CodeSwitchX.UI.Yard;

/// <summary>
/// Where the keyboard goes when the GitHub list opens and closes (#362). Opening takes the keyboard into the panel, so Escape
/// closes it, and puts the hover preview away. Closing hands the keyboard back to the pill that opened the list, as that pill
/// is now: a tile rebuilds its buttons when a git line changes, so the pill is looked up again by its repository and kind, and
/// the tile gets the keyboard when the pill is gone. Nothing is handed back when the keyboard was not in the panel (it may
/// have moved to Raven's type box), when another list took the panel's place, or when the Yard is being left.
/// </summary>
internal sealed class GitHubPanelFocus
{
    private readonly UIElement _root;
    private readonly UIElement _panel;
    private (GitHubRepo Repo, GitHubItemKind Kind)? _opener;

    /// <param name="root">The Yard, whose pills open the list.</param>
    /// <param name="panel">The list's panel, whose visibility says when the list opens and closes.</param>
    public GitHubPanelFocus(UIElement root, UIElement panel)
    {
        _root = root;
        _panel = panel;
        // Raised before the pill's command opens the list, so the panel knows which pill to hand the keyboard back to.
        root.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnClick));
        panel.IsVisibleChanged += OnPanelVisibleChanged;
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Button { DataContext: GitHubPill pill })
        {
            _opener = (pill.Activity.Repo, pill.Kind);
        }
    }

    private void OnPanelVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            // The preview is topmost and would sit over the rows.
            GitHubPillPreview.Close();
            _panel.Focus();
            return;
        }

        // Read now: the keyboard leaves a collapsing panel a moment later.
        var keyboardWasInPanel = _panel.IsKeyboardFocusWithin;
        // Later, once what follows the close has happened: a replacing list shows the panel again, leaving the Yard hides it.
        _root.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => HandBack(keyboardWasInPanel));
    }

    private void HandBack(bool keyboardWasInPanel)
    {
        if (_panel.IsVisible)
        {
            return; // another list replaced this one, and the pill that opened it is the one to return to
        }

        var opener = _opener;
        _opener = null;
        if (opener is not { } open || !keyboardWasInPanel || !_root.IsVisible)
        {
            return;
        }

        if (FindPill(_root, open) is { } pill)
        {
            Keyboard.Focus(pill);
        }
        else if (FindTile(_root, open.Repo) is { } tile && FirstStop(tile) is { } stop)
        {
            Keyboard.Focus(stop);
        }
    }

    private static Button? FindPill(DependencyObject parent, (GitHubRepo Repo, GitHubItemKind Kind) opener)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button { DataContext: GitHubPill pill, IsVisible: true } button && pill.Kind == opener.Kind && pill.Activity.Repo == opener.Repo)
            {
                return button;
            }

            if (FindPill(child, opener) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static UIElement? FirstStop(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement { Focusable: true, IsEnabled: true, IsVisible: true } element && KeyboardNavigation.GetIsTabStop(element))
            {
                return element;
            }

            if (FirstStop(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>The element of the tile that has a line in this repository.</summary>
    private static FrameworkElement? FindTile(DependencyObject parent, GitHubRepo repo)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement { DataContext: WorkspaceTileViewModel tile, IsVisible: true } element
                && tile.GitLines.Any(line => line.Issues?.Activity.Repo == repo || line.PullRequests?.Activity.Repo == repo))
            {
                return element;
            }

            if (FindTile(child, repo) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
