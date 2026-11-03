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
/// closes it, and puts the hover preview away. A list that swaps its rows loses the focused row with them: the keyboard goes
/// back to the panel. Closing it by the keyboard (Escape, Enter or Space on the close button) hands the keyboard back to the pill
/// that opened the list, as its tile has it now: a tile rebuilds its buttons when a git line changes, so the pill is looked up
/// again in its own tile, and the tile gets the keyboard when no pill of the repository is left. Nothing is handed back after a
/// click, when the keyboard was not in the panel (it may have moved to Raven's type box), when another list took the panel's
/// place, or when the Yard is being left.
/// </summary>
internal sealed class GitHubPanelFocus
{
    private readonly UIElement _root;
    private readonly UIElement _panel;
    private readonly Action _closePreview;
    private (FrameworkElement? Tile, GitHubRepo Repo, GitHubItemKind Kind)? _opener;
    private bool _keyboardClose;

    /// <param name="root">The Yard, whose pills open the list.</param>
    /// <param name="panel">The list's panel, whose visibility says when the list opens and closes.</param>
    /// <param name="closePreview">Puts the hover preview away; the preview itself unless a test brings its own, since the preview is one popup shared by every thread.</param>
    public GitHubPanelFocus(UIElement root, UIElement panel, Action? closePreview = null)
    {
        _root = root;
        _panel = panel;
        _closePreview = closePreview ?? GitHubPillPreview.Close;
        // Raised before the pill's command opens the list, so the panel knows which pill to hand the keyboard back to.
        root.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnClick));
        root.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, e) => _keyboardClose = e.Key is Key.Escape or Key.Enter or Key.Space));
        root.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => _keyboardClose = false));
        panel.IsVisibleChanged += OnPanelVisibleChanged;
        panel.IsKeyboardFocusWithinChanged += OnKeyboardFocusWithinChanged;
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Button { DataContext: GitHubPill pill } button)
        {
            // The outermost element with a tile for its context: the tile itself, not a part of it.
            var tile = Ancestors(button).OfType<FrameworkElement>().LastOrDefault(element => element.DataContext is WorkspaceTileViewModel);
            _opener = (tile, pill.Activity.Repo, pill.Kind);
        }
    }

    private void OnPanelVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            // The preview is topmost and would sit over the rows.
            _closePreview();
            _keyboardClose = false;
            _panel.Focus();
            return;
        }

        // Read now: the keyboard leaves a collapsing panel a moment later.
        var handBack = _panel.IsKeyboardFocusWithin && _keyboardClose;
        // Later, once what follows the close has happened: a replacing list shows the panel again, leaving the Yard hides it.
        _root.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => HandBack(handBack));
    }

    /// <summary>The list swapped its rows, and the button that had the keyboard went with them; the keyboard is left alone when it went to another control.</summary>
    private void OnKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false && _panel.IsVisible)
        {
            _root.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_panel.IsVisible && Keyboard.FocusedElement is not UIElement { Focusable: true })
                {
                    _panel.Focus();
                }
            });
        }
    }

    private void HandBack(bool handBack)
    {
        if (_panel.IsVisible)
        {
            return; // another list replaced this one, and the pill that opened it is the one to return to
        }

        var opener = _opener;
        _opener = null;
        if (!handBack || opener is not { Tile: { IsVisible: true } tile } open || !_root.IsVisible)
        {
            return;
        }

        var pills = Descendants(tile).OfType<Button>().Where(button => button.IsVisible && button.DataContext is GitHubPill pill && pill.Activity.Repo == open.Repo).ToList();
        // The same pill, else the repository's other one on the line, else wherever the tile starts.
        var target = pills.FirstOrDefault(button => ((GitHubPill)button.DataContext).Kind == open.Kind) ?? pills.FirstOrDefault();
        if (target is not null)
        {
            Keyboard.Focus(target);
        }
        else
        {
            tile.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            yield return parent;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent) =>
        Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(parent)).Select(i => VisualTreeHelper.GetChild(parent, i)).SelectMany(child => Descendants(child).Prepend(child));
}
