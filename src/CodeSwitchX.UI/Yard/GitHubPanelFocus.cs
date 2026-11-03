using System.Windows;
using System.Windows.Input;

namespace CodeSwitchX.UI.Yard;

/// <summary>
/// Where the keyboard goes when the GitHub list opens and closes (#362). Opening takes the keyboard into the panel, so Escape
/// closes it, and puts the hover preview away; closing hands the keyboard back to the pill that opened the list, when that pill
/// is still on screen (the tile may have been rebuilt meanwhile).
/// </summary>
internal sealed class GitHubPanelFocus
{
    private WeakReference<UIElement>? _opener;

    /// <summary>The pill that was clicked, or invoked by the keyboard, to open the list.</summary>
    public void Opened(UIElement pill) => _opener = new WeakReference<UIElement>(pill);

    public void PanelShown(UIElement panel)
    {
        // The preview is topmost and would sit over the rows.
        GitHubPillPreview.Close();
        panel.Focus();
    }

    public void PanelHidden()
    {
        var opener = _opener is { } weak && weak.TryGetTarget(out var pill) ? pill : null;
        _opener = null;
        if (opener is { IsVisible: true } && PresentationSource.FromVisual(opener) is not null)
        {
            Keyboard.Focus(opener);
        }
    }
}
