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

/// <summary>#362: closing the GitHub list hands the keyboard back to the pill that opened it, as the tile has it now.</summary>
public sealed class GitHubPanelFocusTests
{
    private static GitHubPill Pill(string repo, GitHubItemKind kind = GitHubItemKind.Issue) =>
        new(new RepoActivity(new GitHubRepo("acme", repo), "me", 3, 3, [], []), kind);

    /// <summary>
    /// The Yard in little: a tile with a pill and a chat button, Raven's type box beside it, and the list's panel with a button
    /// in it, all under a window of their own, which keeps the keyboard focus as far as WPF is concerned.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        private readonly HwndSource _source;

        public Rig()
        {
            Pill = NewPill(GitHubPanelFocusTests.Pill("app"));
            Chat = new Button();
            Tile = new StackPanel { Children = { Pill, Chat } };
            Type = new TextBox();
            InPanel = new Button();
            Panel = new Border { Focusable = true, Visibility = Visibility.Collapsed, Child = InPanel };
            Root = new Grid { Children = { Tile, Type, Panel } };
            _source = new HwndSource(new HwndSourceParameters("test") { WindowStyle = 0 }) { RootVisual = Root };
            _ = new GitHubPanelFocus(Root, Panel);
        }

        public Grid Root { get; }
        public StackPanel Tile { get; }
        public Button Pill { get; set; }
        public Button Chat { get; }
        public TextBox Type { get; }
        public Border Panel { get; }
        public Button InPanel { get; }

        public static Button NewPill(GitHubPill pill) => new() { DataContext = pill };

        /// <summary>What a click on the pill does: the Click, then the list showing its panel, which takes the keyboard in.</summary>
        public void Open(Button? pill = null)
        {
            (pill ?? Pill).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, pill ?? Pill));
            Panel.Visibility = Visibility.Visible;
            Keyboard.Focus(InPanel);
        }

        public void Close()
        {
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

    [Fact]
    public Task Closing_the_list_puts_the_keyboard_back_on_the_pill() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        rig.Open();

        rig.Close();

        Keyboard.FocusedElement.ShouldBeSameAs(rig.Pill);
    });

    [Fact]
    public Task A_pill_the_tile_rebuilt_gets_the_keyboard_not_the_one_that_was_replaced() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        rig.Open();
        var old = rig.Pill;
        rig.Tile.Children.Remove(old);
        // a changed count makes a new record and a new button for the same repository and kind
        rig.Pill = Rig.NewPill(Pill("app") with { Activity = ((GitHubPill)old.DataContext).Activity with { OpenIssues = 4 } });
        rig.Tile.Children.Insert(0, rig.Pill);

        rig.Close();

        Keyboard.FocusedElement.ShouldBeSameAs(rig.Pill);
    });

    [Fact]
    public Task A_pill_that_is_gone_hands_the_keyboard_to_its_tile() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        var tile = new WorkspaceTileViewModel(new Workspace { Name = "A", RootPath = @"c:\a" }, null!);
        tile.ShowGit([new GitLine("app", "main", 0, Issues: Pill("app"), PullRequests: Pill("app", GitHubItemKind.PullRequest))]);
        var tileElement = new StackPanel { DataContext = tile };
        rig.Root.Children.Remove(rig.Tile);
        rig.Tile.Children.Remove(rig.Chat);
        tileElement.Children.Add(rig.Chat);
        rig.Root.Children.Add(tileElement);
        var pill = Rig.NewPill(Pill("app"));
        tileElement.Children.Add(pill);
        rig.Open(pill);
        tileElement.Children.Remove(pill); // the line no longer has the pill: no button holds it any more

        rig.Close();

        Keyboard.FocusedElement.ShouldBeSameAs(rig.Chat);
    });

    [Fact]
    public Task A_pill_that_left_the_tree_and_no_tile_gets_nothing_and_throws_nothing() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        rig.Open();
        rig.Tile.Children.Remove(rig.Pill);

        Should.NotThrow(rig.Close);

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
        rig.Panel.Visibility = Visibility.Collapsed;
        rig.Root.Visibility = Visibility.Collapsed;
        Rig.Drain();

        Keyboard.FocusedElement.ShouldNotBe(rig.Pill);
    });

    [Fact]
    public Task A_list_that_replaces_another_keeps_the_pill_that_opened_it() => StaThread.RunAsync(() =>
    {
        using var rig = new Rig();
        var second = Rig.NewPill(Pill("app", GitHubItemKind.PullRequest));
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
}
