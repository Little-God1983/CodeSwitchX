using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using CodeSwitchX.UI.Tests.Infrastructure;
using CodeSwitchX.UI.Yard;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>#362: closing the GitHub list hands the keyboard back to the pill that opened it.</summary>
public sealed class GitHubPanelFocusTests
{
    /// <summary>Two buttons under a window of their own, which keeps the keyboard focus as far as WPF is concerned.</summary>
    private static (HwndSource Source, Button Pill, Button Other) Shown()
    {
        var pill = new Button();
        var other = new Button();
        var source = new HwndSource(new HwndSourceParameters("test") { WindowStyle = 0 })
        {
            RootVisual = new StackPanel { Children = { pill, other } },
        };
        return (source, pill, other);
    }

    [Fact]
    public Task Closing_the_list_puts_the_keyboard_back_on_the_pill() => StaThread.RunAsync(() =>
    {
        var (source, pill, other) = Shown();
        using var _ = source;
        var focus = new GitHubPanelFocus();
        focus.Opened(pill);
        Keyboard.Focus(other);

        focus.PanelHidden();

        Keyboard.FocusedElement.ShouldBeSameAs(pill);
    });

    [Fact]
    public Task A_pill_that_left_the_tree_gets_nothing_and_throws_nothing() => StaThread.RunAsync(() =>
    {
        var (source, pill, other) = Shown();
        using var _ = source;
        var focus = new GitHubPanelFocus();
        focus.Opened(pill);
        ((StackPanel)pill.Parent).Children.Remove(pill);
        Keyboard.Focus(other);

        Should.NotThrow(focus.PanelHidden);

        Keyboard.FocusedElement.ShouldBeSameAs(other);
    });

    [Fact]
    public Task Closing_without_an_opener_leaves_the_keyboard_alone() => StaThread.RunAsync(() =>
    {
        var (source, _, other) = Shown();
        using var _ = source;
        Keyboard.Focus(other);

        Should.NotThrow(new GitHubPanelFocus().PanelHidden);

        Keyboard.FocusedElement.ShouldBeSameAs(other);
    });

    [Fact]
    public Task The_pill_is_given_back_once() => StaThread.RunAsync(() =>
    {
        var (source, pill, other) = Shown();
        using var _ = source;
        var focus = new GitHubPanelFocus();
        focus.Opened(pill);
        focus.PanelHidden();
        Keyboard.Focus(other);

        focus.PanelHidden();

        Keyboard.FocusedElement.ShouldBeSameAs(other);
    });
}
