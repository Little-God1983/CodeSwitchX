using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodeSwitchX.UI.Raven;

/// <summary>
/// The logic of the Push to talk | Open mic switch, whose halves are radio buttons that show the panel's mode (a one-way
/// binding) and carry their mode as their <see cref="FrameworkElement.Tag"/>. A half checked from outside the panel (UI
/// Automation's Select, which raises no click) is the user's choice, as a click is, one on the half already checked
/// too (after a fallback). The panel's own update of the switch checks the half of the mode it is in, which is no choice.
/// </summary>
internal sealed class MicModeSwitch(Func<RavenPanelViewModel?> viewModel)
{
    /// <summary>A half was just checked and chosen, so the click that checked it must not choose again.</summary>
    private bool _chosenByCheck;

    public void OnChecked(object half)
    {
        if (viewModel() is { } panel && half is RadioButton { Tag: MicMode mode } button && mode != panel.MicMode)
        {
            _chosenByCheck = true;
            button.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _chosenByCheck = false); // the click that checked it follows at once
            panel.ChooseMicModeCommand.Execute(mode);
        }
    }

    public void OnClicked(object half)
    {
        if (_chosenByCheck)
        {
            _chosenByCheck = false;
        }
        else if (viewModel() is { } panel && half is RadioButton { Tag: MicMode mode })
        {
            panel.ChooseMicModeCommand.Execute(mode);
        }
    }
}
