using System.Windows;
using CodeSwitchX.UI.Infrastructure;

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
    /// minimized, and a minimized owner hides the windows it owns: it comes up first, and goes back down when the answer is
    /// Cancel.
    /// </summary>
    public static ExitChoice Ask(Window owner, string question, string detail)
    {
        var wasMinimized = owner.WindowState == WindowState.Minimized;
        if (wasMinimized || !owner.IsVisible)
        {
            WindowActivation.BringUp(owner);
        }

        var dialog = new ExitWindow(question, detail) { Owner = owner };
        dialog.ShowDialog();
        if (dialog.Choice == ExitChoice.Cancel && wasMinimized)
        {
            owner.WindowState = WindowState.Minimized;
        }

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
