using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace CodeSwitchX.UI.Raven;

/// <summary>
/// One half of the Push to talk | Open mic switch. A plain radio button's UI Automation Select only checks it, which
/// does nothing on the half already checked (after a fallback) though a click there is a choice. This one's Select is
/// a click, so a screen reader chooses as the mouse does.
/// </summary>
internal sealed class MicModeRadioButton : RadioButton
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    /// <summary>A radio button's peer whose Select clicks the half: the check, if it was not checked, then the click.</summary>
    private sealed class Peer(MicModeRadioButton owner) : RadioButtonAutomationPeer(owner), ISelectionItemProvider
    {
        bool ISelectionItemProvider.IsSelected => owner.IsChecked == true;

        IRawElementProviderSimple? ISelectionItemProvider.SelectionContainer => null;

        void ISelectionItemProvider.Select()
        {
            if (!IsEnabled())
            {
                throw new ElementNotEnabledException();
            }

            owner.OnClick();
        }

        void ISelectionItemProvider.AddToSelection()
        {
            if (owner.IsChecked != true)
            {
                throw new InvalidOperationException("A radio button cannot be added to a selection; select it instead.");
            }
        }

        void ISelectionItemProvider.RemoveFromSelection()
        {
            if (owner.IsChecked == true)
            {
                throw new InvalidOperationException("A checked radio button cannot be removed from the selection.");
            }
        }
    }
}
