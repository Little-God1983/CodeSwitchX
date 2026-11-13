using System.Windows;

namespace CodeSwitchX.UI.Shell;

/// <summary>Asks whether Exit closes the VS Code windows too, or only CodeSwitchX (#420).</summary>
public partial class ExitWindow : Window
{
    public ExitWindow(string question, string detail)
    {
        InitializeComponent();
        Question.Text = question;
        Detail.Text = detail;
    }

    /// <summary>What the user chose; Cancel when the window is closed otherwise.</summary>
    public ExitChoice Choice { get; private set; } = ExitChoice.Cancel;

    /// <summary>
    /// Shows the question over <paramref name="owner"/> until it is answered. The tray's Exit can come while the owner is
    /// minimized, which would hide a window it owns; restoring it would bring the Cab's VS Code up too, over the question.
    /// So then the question stands on its own, centred, in the taskbar and on top: the shell, restored meanwhile by the tray
    /// or a hotkey, takes no input while it is open, and the Cab's VS Code must not cover it. The shell stays as it was.
    /// </summary>
    public static ExitChoice Ask(Window owner, string question, string detail)
    {
        var dialog = new ExitWindow(question, detail);
        if (owner.WindowState != WindowState.Minimized && owner.IsVisible)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowInTaskbar = true;
            dialog.Topmost = true;
        }

        dialog.ShowDialog();
        return dialog.Choice;
    }

    private void CloseAll(object sender, RoutedEventArgs e) => Choose(ExitChoice.CloseAll);

    private void CloseOnly(object sender, RoutedEventArgs e) => Choose(ExitChoice.CloseOnly);

    private void Cancel(object sender, RoutedEventArgs e) => Choose(ExitChoice.Cancel);

    private void Choose(ExitChoice choice)
    {
        Choice = choice;
        Close();
    }
}
