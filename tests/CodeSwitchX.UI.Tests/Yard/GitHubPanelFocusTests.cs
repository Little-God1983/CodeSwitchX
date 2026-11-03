using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CodeSwitchX.Core.GitHub;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Tests.Infrastructure;
using CodeSwitchX.UI.Yard;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>#362: closing the GitHub list by the keyboard hands the keyboard back to the pill that opened it, as its tile has it now.</summary>
public sealed class GitHubPanelFocusTests
{
    private static GitHubPill Pill(GitHubItemKind kind = GitHubItemKind.Issue) =>
        new(new RepoActivity(new GitHubRepo("acme", "app"), "me", 3, 3, [], []), kind);

    private static Button NewPill(GitHubPill pill) => new() { DataContext = pill };

    /// <summary>
    /// The Yard in little: tiles that each hold a pill and a chat button, Raven's type box beside them, and the list's panel
    /// with a button in it, all under a window of their own, which keeps the keyboard focus as far as WPF is concerned.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        private readonly HwndSource _source;

        public Rig()
        {
            Type = new TextBox();
            InPanel = new Button();
            Panel = new Border { Focusable = true, Visibility = Visibility.Collapsed, Child = InPanel };
            Root = new Grid { Children = { Type, Panel } };
            _source = new HwndSource(new HwndSourceParameters("test") { WindowStyle = 0 }) { RootVisual = Root };
            _ = new GitHubPanelFocus(Root, Panel, () => { });
            Tile = AddTile("A");
            Pill = (Button)Tile.Children[0];
            Chat = (Button)Tile.Children[1];
        }

        public Grid Root { get; }
        public StackPanel Tile { get; }
        public Button Pill { get; }
        public Button Chat { get; }
        public TextBox Type { get; }
        public Border Panel { get; }
        public Button InPanel { get; }

        /// <summary>A tile with an issue pill and a chat button; its git line has the pill, as the app's does.</summary>
        public StackPanel AddTile(string name)
        {
            var model = new WorkspaceTileViewModel(new Workspace { Name = name, RootPath = @"c:\" + name }, null!);
            model.ShowGit([new GitLine("app", "main", 0, Issues: GitHubPanelFocusTests.Pill())]);
            var tile = new StackPanel { DataContext = model, Children = { NewPill(GitHubPanelFocusTests.Pill()), new Button() } };
            Root.Children.Add(tile);
            return tile;
        }

        /// <summary>What a click on the pill does: the Click, then the list showing its panel, which takes the keyboard in.</summary>
        public void Open(Button? pill = null)
        {
            (pill ?? Pill).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, pill ?? Pill));
            Panel.Visibility = Visibility.Visible;
            Keyboard.Focus(InPanel);
        }

        public void Press(Key key) => InPanel.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, _source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });

        public void ClickMouse() => InPanel.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });

        /// <summary>A key that closes the list, which the Yard answers by collapsing the panel.</summary>
        public void Close(Key key = Key.Escape)
        {
            Press(key);
            Panel.Visibility = Visibility.Collapsed;
            Drain();
        }

        /// <summary>The hand-back waits for the dispatcher.</summary>
        public static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        public void Dispose() => _source.Dispose();
    }

    [Fact]
    public Task Opening_the_list_takes_the_keyboard_into_the_panel() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        rig.Panel.Visibility = Visibility.Visible;

        Keyboard.FocusedElement.ShouldBeSameAs(rig.Panel);
    });

    [Theory]
    [InlineData(Key.Escape)]
    [InlineData(Key.Space)]
    [InlineData(Key.Enter)]
    public Task Closing_the_list_by_the_keyboard_puts_it_back_on_the_pill(Key key) => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        rig.Open();

        rig.Close(key);

        Keyboard.FocusedElement.ShouldBeSameAs(rig.Pill);
    });

    [Fact]
    public Task Closing_the_list_with_the_mouse_hands_nothing_back() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        rig.Open();

        rig.ClickMouse();
        rig.Panel.Visibility = Visibility.Collapsed;
        Rig.Drain();

        Keyboard.FocusedElement.ShouldNotBe(rig.Pill);
    });

    [Fact]
    public Task A_mouse_click_after_a_key_still_closes_without_a_hand_back() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        rig.Open();
        rig.Press(Key.Enter); // opens a row's page, the list stays

        rig.ClickMouse();
        rig.Panel.Visibility = Visibility.Collapsed;
        Rig.Drain();

        Keyboard.FocusedElement.ShouldNotBe(rig.Pill);
    });

    [Fact]
    public Task A_pill_the_tile_rebuilt_gets_the_keyboard_not_the_one_that_was_replaced() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        rig.Open();
        rig.Tile.Children.Remove(rig.Pill);
        // a changed count makes a new record and a new button for the same repository and kind
        var rebuilt = NewPill(Pill() with { Activity = Pill().Activity with { OpenIssues = 4 } });
        rig.Tile.Children.Insert(0, rebuilt);

        rig.Close();

        Keyboard.FocusedElement.ShouldBeSameAs(rebuilt);
    });

    [Fact]
    public Task Two_tiles_on_one_repository_hand_the_keyboard_to_the_pill_that_was_clicked() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        var second = (Button)rig.AddTile("B").Children[0];
        rig.Open(second);

        rig.Close();

        Keyboard.FocusedElement.ShouldBeSameAs(second);
    });

    [Fact]
    public Task A_tile_whose_last_pill_is_gone_gets_the_keyboard_itself() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        rig.Open();
        // the repository has no open item any more: the line has no pill, and no button holds it
        ((WorkspaceTileViewModel)rig.Tile.DataContext).ShowGit([new GitLine("app", "main", 0)]);
        rig.Tile.Children.Remove(rig.Pill);

        rig.Close();

        Keyboard.FocusedElement.ShouldBeSameAs(rig.Chat);
    });

    [Fact]
    public Task A_pill_that_is_gone_hands_the_keyboard_to_the_repositorys_other_pill() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        var requests = NewPill(Pill(GitHubItemKind.PullRequest));
        rig.Tile.Children.Add(requests);
        rig.Open();
        rig.Tile.Children.Remove(rig.Pill);

        rig.Close();

        Keyboard.FocusedElement.ShouldBeSameAs(requests);
    });

    [Fact]
    public Task A_tile_that_is_gone_too_gets_nothing_and_throws_nothing() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        rig.Open();
        rig.Root.Children.Remove(rig.Tile);

        Should.NotThrow(() => rig.Close());

        Keyboard.FocusedElement.ShouldNotBe(rig.Pill);
    });

    [Fact]
    public Task Keyboard_that_moved_elsewhere_while_the_list_was_open_stays_there() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        rig.Open();
        Keyboard.Focus(rig.Type);

        rig.Close();

        Keyboard.FocusedElement.ShouldBeSameAs(rig.Type);
    });

    [Fact]
    public Task Leaving_the_yard_with_the_list_open_hands_the_keyboard_to_nothing() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        rig.Open();

        // the shell closes the list, then collapses the Yard
        rig.Press(Key.Escape);
        rig.Panel.Visibility = Visibility.Collapsed;
        rig.Root.Visibility = Visibility.Collapsed;
        Rig.Drain();

        Keyboard.FocusedElement.ShouldNotBe(rig.Pill);
    });

    [Fact]
    public Task A_list_that_replaces_another_keeps_the_pill_that_opened_it() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        var second = NewPill(Pill(GitHubItemKind.PullRequest));
        rig.Tile.Children.Insert(1, second);
        rig.Open();

        // a click on another pill: its Click, the first list closing, the second list showing the panel again, all in a row
        second.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, second));
        rig.Panel.Visibility = Visibility.Collapsed;
        rig.Panel.Visibility = Visibility.Visible;
        Keyboard.Focus(rig.InPanel);
        Rig.Drain();
        Keyboard.FocusedElement.ShouldBeSameAs(rig.InPanel);

        rig.Close();

        Keyboard.FocusedElement.ShouldBeSameAs(second);
    });

    [Fact]
    public Task Closing_without_an_opener_leaves_the_keyboard_alone() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        rig.Panel.Visibility = Visibility.Visible;
        Keyboard.Focus(rig.InPanel);

        rig.Close();

        Keyboard.FocusedElement.ShouldNotBe(rig.Pill);
    });

    [Fact]
    public Task The_pill_is_given_back_once() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        rig.Open();
        rig.Close();
        rig.Panel.Visibility = Visibility.Visible;
        Keyboard.Focus(rig.InPanel);

        rig.Close();

        Keyboard.FocusedElement.ShouldNotBe(rig.Pill);
    });

    [Fact]
    public Task A_list_that_swaps_its_rows_keeps_the_keyboard_in_the_panel() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        rig.Open();

        // the rows are rebuilt: the button that had the keyboard is gone
        rig.Panel.Child = new Button();
        Rig.Drain();

        Keyboard.FocusedElement.ShouldBeSameAs(rig.Panel);
    });

    [Fact]
    public Task A_swap_does_not_pull_the_keyboard_back_from_another_control() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        rig.Open();
        Keyboard.Focus(rig.Type);
        Rig.Drain();

        Keyboard.FocusedElement.ShouldBeSameAs(rig.Type);
    });
}
