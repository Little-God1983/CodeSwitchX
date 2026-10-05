using System.Windows;
using CodeSwitchX.UI.Shell;

namespace CodeSwitchX.UI.Workspaces;

/// <summary>Asks what becomes of a window's open cards before the workspace is removed (#135).</summary>
public partial class RemoveWorkspaceWindow : Window
{
    public RemoveWorkspaceWindow(string workspace, int cards)
    {
        InitializeComponent();
        Question.Text = cards == 1
            ? $"A card waits in {workspace}'s Raven chat: a question or permission prompt of one of its chats."
            : $"{cards} cards wait in {workspace}'s Raven chat: questions or permission prompts of its chats.";
    }

    /// <summary>What the user chose; Cancel when the window is closed otherwise.</summary>
    public RemoveChoice Choice { get; private set; } = RemoveChoice.Cancel;

    /// <summary>Shows the question over <paramref name="owner"/> until it is answered.</summary>
    public static RemoveChoice Ask(Window owner, string workspace, int cards)
    {
        var dialog = new RemoveWorkspaceWindow(workspace, cards) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Choice;
    }

    private void AnswerFirst(object sender, RoutedEventArgs e) => Choose(RemoveChoice.AnswerFirst);

    private void LeaveToVsCode(object sender, RoutedEventArgs e) => Choose(RemoveChoice.LeaveToVsCode);

    private void Cancel(object sender, RoutedEventArgs e) => Choose(RemoveChoice.Cancel);

    private void Choose(RemoveChoice choice)
    {
        Choice = choice;
        Close();
    }
}
