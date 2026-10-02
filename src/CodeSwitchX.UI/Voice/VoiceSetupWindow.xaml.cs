using System.Windows;

namespace CodeSwitchX.UI.Voice;

public partial class VoiceSetupWindow : Window
{
    public VoiceSetupWindow(VoiceSetupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += Close;
        Closed += (_, _) => viewModel.Dispose();
    }
}
