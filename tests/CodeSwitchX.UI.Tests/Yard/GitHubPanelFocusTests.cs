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
[Collection(KeyboardFocusCollection.Name)]
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
            Panel = new Border { Focusable = false, Visibility = Visibility.Collapsed, Child = InPanel };
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

        public void Press(Key key) => UseDevice(Keyboard.PrimaryDevice);

        public void ClickMouse() => UseDevice(Mouse.PrimaryDevice);

        /// <summary>
        /// Says which device was used last. The input manager sets that from real input only (an event raised or processed by hand
        /// leaves it empty), and its setter is internal.
        /// </summary>
        private static void UseDevice(InputDevice device) =>
            typeof(InputManager).GetProperty(nameof(InputManager.MostRecentInputDevice))!.SetMethod!.Invoke(InputManager.Current, [device]);

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
        Rig.Drain(); // a panel that just appeared has no layout yet: the keyboard is taken in once it has

        Keyboard.FocusedElement.ShouldBeSameAs(rig.InPanel);
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
    public Task A_close_without_any_input_counts_as_a_keyboard_close() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        rig.Open();

        // what UI Automation does: the list closes, and no key or click came with it
        rig.Panel.Visibility = Visibility.Collapsed;
        Rig.Drain();

        Keyboard.FocusedElement.ShouldBeSameAs(rig.Pill);
    });

    [Fact]
    public Task A_click_into_another_control_straight_after_the_close_keeps_the_caret_there() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        rig.Open();

        rig.Press(Key.Escape);
        rig.Panel.Visibility = Visibility.Collapsed;
        Keyboard.Focus(rig.Type); // before the hand-back has run
        Rig.Drain();

        Keyboard.FocusedElement.ShouldBeSameAs(rig.Type);
    });

    [Fact]
    public Task A_tile_that_moved_to_another_group_while_the_list_was_open_gets_the_keyboard() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        rig.Open();
        // a new element for the same tile, as a tile that is removed and added again gets
        rig.Root.Children.Remove(rig.Tile);
        var moved = new StackPanel { DataContext = rig.Tile.DataContext };
        var pill = NewPill(Pill());
        moved.Children.Add(pill);
        rig.Root.Children.Add(moved);

        rig.Close();

        Keyboard.FocusedElement.ShouldBeSameAs(pill);
    });

    [Fact]
    public Task Two_lines_of_one_tile_on_the_same_repository_hand_the_keyboard_to_the_line_that_was_clicked() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        var model = new WorkspaceTileViewModel(new Workspace { Name = "W", RootPath = @"c:\w" }, null!);
        var (first, second) = (NewPill(Pill()), NewPill(Pill()));
        rig.Root.Children.Add(new StackPanel
        {
            DataContext = model,
            Children =
            {
                new StackPanel { DataContext = new GitLine("app", "main", 0, @"c:\w\one", Issues: Pill()), Children = { first } },
                new StackPanel { DataContext = new GitLine("app", "main", 0, @"c:\w	wo", Issues: Pill()), Children = { second } },
            },
        });
        rig.Open(second);

        rig.Close();

        Keyboard.FocusedElement.ShouldBeSameAs(second);
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
    public Task Closing_a_panel_a_line_opened_hands_the_keyboard_back_to_that_line() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        var model = new WorkspaceTileViewModel(new Workspace { Name = "W", RootPath = @"c:\w" }, null!);
        var line = new Button { DataContext = new GitLine("app", "main", 0, @"c:\w\one", Issues: Pill(), GitHub: Pill().Activity), Content = NewPill(Pill()) };
        rig.Root.Children.Add(new StackPanel { DataContext = model, Children = { line } });
        rig.Open(line);

        rig.Close();

        Keyboard.FocusedElement.ShouldBeSameAs(line);
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
}
