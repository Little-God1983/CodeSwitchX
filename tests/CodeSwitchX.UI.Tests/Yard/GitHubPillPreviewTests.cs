using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CodeSwitchX.Core.GitHub;
using CodeSwitchX.UI.Yard;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>#335: the preview is topmost, so it must not outlive the moment it belongs to. No window here is ever shown.</summary>
public sealed class GitHubPillPreviewTests(GitHubPillPreviewTests.PreviewThread thread) : IClassFixture<GitHubPillPreviewTests.PreviewThread>
{
    /// <summary>The preview keeps one popup, which belongs to the thread that made it, so every test of this class runs on the same one.</summary>
    public sealed class PreviewThread : IDisposable
    {
        private readonly Action<Popup, bool> _realShow = GitHubPillPreview.Show;
        private readonly Dispatcher _dispatcher;

        public PreviewThread()
        {
            var ready = new TaskCompletionSource<Dispatcher>();
            var thread = new Thread(() =>
            {
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }) { IsBackground = true, Name = "test-preview" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            _dispatcher = ready.Task.GetAwaiter().GetResult();
            GitHubPillPreview.Show = (_, open) => Shown = open;
        }

        public bool Shown { get; private set; }

        public Task RunAsync(Action body) => _dispatcher.InvokeAsync(body).Task;

        public void Dispose()
        {
            GitHubPillPreview.Show = _realShow;
            _dispatcher.InvokeShutdown();
        }
    }

    /// <summary>Can say it lost the activation, which a window that was never shown otherwise cannot.</summary>
    private sealed class TestWindow : Window
    {
        public void Deactivate() => OnDeactivated(EventArgs.Empty);
    }

    private static GitHubPill Pill() => new(new RepoActivity(new GitHubRepo("acme", "tool"), "me", 3, 3, [], []), GitHubItemKind.Issue);

    private static Button PillButton()
    {
        var button = new Button { DataContext = Pill() };
        GitHubPillPreview.SetIsEnabled(button, true);
        return button;
    }

    /// <summary>A window holding one pill per argument, each with the row template the preview looks up.</summary>
    private static TestWindow WindowWith(params Button[] pills)
    {
        var panel = new StackPanel();
        foreach (var pill in pills)
        {
            panel.Children.Add(pill);
        }

        var window = new TestWindow { Content = panel };
        window.Resources["GitHubRowTemplate"] = new DataTemplate();
        return window;
    }

    private static void Hover(UIElement pill) => pill.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });

    private static void DrainDispatcher() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    [Fact]
    public Task Hovering_a_pill_opens_the_preview() => thread.RunAsync(() =>
    {
        var pill = PillButton();
        _ = WindowWith(pill);

        Hover(pill);

        thread.Shown.ShouldBeTrue();
        GitHubPillPreview.Close();
    });

    [Fact]
    public Task Close_closes_it() => thread.RunAsync(() =>
    {
        var pill = PillButton();
        _ = WindowWith(pill);
        Hover(pill);

        GitHubPillPreview.Close();

        thread.Shown.ShouldBeFalse();
    });

    [Fact]
    public Task The_list_panel_appearing_closes_it() => thread.RunAsync(() =>
    {
        var pill = PillButton();
        _ = WindowWith(pill);
        Hover(pill);
        thread.Shown.ShouldBeTrue();

        var panel = new Border { Focusable = true, Visibility = Visibility.Collapsed };
        using var source = new HwndSource(new HwndSourceParameters("test") { WindowStyle = 0 }) { RootVisual = new Grid { Children = { panel } } };
        _ = new GitHubPanelFocus((UIElement)source.RootVisual, panel);

        panel.Visibility = Visibility.Visible;

        thread.Shown.ShouldBeFalse();
    });

    [Fact]
    public Task A_pill_invoked_through_UI_Automation_closes_it() => thread.RunAsync(() =>
    {
        var pill = PillButton();
        _ = WindowWith(pill);
        Hover(pill);

        ((IInvokeProvider)new ButtonAutomationPeer(pill)).Invoke();
        DrainDispatcher();

        var shown = thread.Shown;
        GitHubPillPreview.Close();
        shown.ShouldBeFalse();
    });

    [Fact]
    public Task A_window_that_loses_the_activation_closes_it() => thread.RunAsync(() =>
    {
        var pill = PillButton();
        var window = WindowWith(pill);
        Hover(pill);

        window.Deactivate();

        var shown = thread.Shown;
        GitHubPillPreview.Close();
        shown.ShouldBeFalse();
    });

    [Fact]
    public Task Moving_to_another_pill_still_closes_on_deactivation() => thread.RunAsync(() =>
    {
        var first = PillButton();
        var second = PillButton();
        var window = WindowWith(first, second);
        Hover(first);
        Hover(second);

        window.Deactivate();

        var shown = thread.Shown;
        GitHubPillPreview.Close();
        shown.ShouldBeFalse();
    });

    [Fact]
    public Task A_window_lets_go_of_a_closed_preview() => thread.RunAsync(() =>
    {
        var pill = PillButton();
        var other = PillButton();
        var window = WindowWith(pill);
        _ = WindowWith(other);
        Hover(pill);
        GitHubPillPreview.Close();
        Hover(other);

        window.Deactivate();

        var shown = thread.Shown;
        GitHubPillPreview.Close();
        shown.ShouldBeTrue("the first window's deactivation is no business of a preview on another window");
    });
}
