using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodeSwitchX.Core.GitHub;

namespace CodeSwitchX.UI.Yard;

/// <summary>
/// Where the keyboard goes when the GitHub list opens and closes (#362). Opening takes the keyboard to the first stop in the panel, so Escape
/// closes it, and puts the hover preview away. A list that swaps its rows loses the focused row with them, and the keyboard
/// falls to the window: it goes back to the panel. Closing the list hands the keyboard back to the repository line or pill that opened it (#406), as its
/// tile has it now: a tile rebuilds its buttons when a git line changes, and may be rebuilt itself when it moves to another
/// group, so the line or pill is looked up again, on the same line of the same tile. The tile gets the keyboard when no pill of the
/// repository is left. Nothing is handed back after a mouse close, when the keyboard was not in the panel, when it has moved to
/// a control since (Raven's type box), when another list took the panel's place, or when the Yard is being left.
/// </summary>
internal sealed class GitHubPanelFocus
{
    private readonly UIElement _root;
    private readonly UIElement _panel;
    private readonly Action _closePreview;
    private (WorkspaceTileViewModel? Tile, string? Line, GitHubRepo Repo, GitHubItemKind? Kind)? _opener;

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
        panel.IsVisibleChanged += OnPanelVisibleChanged;
        panel.IsKeyboardFocusWithinChanged += OnKeyboardFocusWithinChanged;
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Button { DataContext: GitHubPill pill } button)
        {
            // The outermost tile context: the tile, not a part of it.
            _opener = (ContextsAbove<WorkspaceTileViewModel>(button).LastOrDefault(), ContextsAbove<GitLine>(button).FirstOrDefault()?.Path, pill.Activity.Repo, pill.Kind);
        }
        else if (e.OriginalSource is Button { DataContext: GitLine { GitHub: { } activity } line } lineButton)
        {
            // A repository line (#406): no kind, the line itself is what the keyboard goes back to.
            _opener = (ContextsAbove<WorkspaceTileViewModel>(lineButton).LastOrDefault(), line.Path, activity.Repo, null);
        }
    }

    private void OnPanelVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            // The preview is topmost and would sit over the rows.
            _closePreview();
            FocusInside();
            return;
        }

        // Read now: the keyboard leaves a collapsing panel a moment later, and what closed the list is the last input.
        var handBack = _panel.IsKeyboardFocusWithin && InputManager.Current.MostRecentInputDevice is not MouseDevice;
        // Later, once what follows the close has happened: a replacing list shows the panel again, leaving the Yard hides it.
        _root.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => HandBack(handBack));
    }

    /// <summary>The list swapped its rows, and the button that had the keyboard went with them: the keyboard falls to the window. Nowhere at all is the app losing the activation, which is left alone.</summary>
    private void OnKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false && _panel.IsVisible)
        {
            _root.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_panel.IsVisible && Keyboard.FocusedElement is { } focused && ReferenceEquals(focused, PresentationSource.FromVisual(_root)?.RootVisual))
                {
                    FocusInside();
                }
            });
        }
    }

    /// <summary>
    /// The keyboard goes to the first stop inside the panel, not to the panel itself: Shift+Tab from the panel would leave it for
    /// the controls under the shade. Just after the panel appears it has no layout yet and no stop to take, so that is tried again
    /// once it has.
    /// </summary>
    private void FocusInside()
    {
        if (!_panel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))
        {
            _root.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_panel.IsVisible && !_panel.IsKeyboardFocusWithin)
                {
                    _panel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
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
        // The keyboard may have gone to a control since (a click into Raven's type box); the window and a control that is gone are nobody's.
        var focused = Keyboard.FocusedElement as UIElement;
        var free = focused is null || !focused.IsVisible || _panel.IsAncestorOf(focused) || ReferenceEquals(focused, PresentationSource.FromVisual(_root)?.RootVisual);
        if (!handBack || !free || !_root.IsVisible || opener is not { Tile: { } model } open)
        {
            return;
        }

        // The tile is looked up again: a tile that moved to another group is a new element.
        if (Descendants(_root).OfType<FrameworkElement>().FirstOrDefault(element => ReferenceEquals(element.DataContext, model) && element.IsVisible) is not { } tile)
        {
            return;
        }

        // A line that opened it gets it back (#406). A pill: that pill of that line, else the line's other pill, else the repository's
        // pill on another line. Else wherever the tile starts.
        var lineButton = open.Kind is null
            ? Descendants(tile).OfType<Button>().FirstOrDefault(button => button.IsVisible && button.DataContext is GitLine { GitHub: { } activity } line && activity.Repo == open.Repo && line.Path == open.Line)
            : null;
        var target = lineButton ?? Descendants(tile).OfType<Button>().Where(button => button.IsVisible && button.DataContext is GitHubPill pill && pill.Activity.Repo == open.Repo)
            .OrderBy(button => ContextsAbove<GitLine>(button).FirstOrDefault()?.Path != open.Line)
            .ThenBy(button => ((GitHubPill)button.DataContext).Kind != open.Kind)
            .FirstOrDefault();
        if (target is not null)
        {
            Keyboard.Focus(target);
        }
        else
        {
            tile.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
    }

    /// <summary>The data contexts of the elements above this one, nearest first.</summary>
    private static IEnumerable<T> ContextsAbove<T>(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if ((parent as FrameworkElement)?.DataContext is T context)
            {
                yield return context;
            }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent) =>
        Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(parent)).Select(i => VisualTreeHelper.GetChild(parent, i)).SelectMany(child => Descendants(child).Prepend(child));
}
