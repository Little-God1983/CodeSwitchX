using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.UI.Shell;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Shell;

/// <summary>Raven minimizes, maximizes and restores the window by voice (#117), and says honestly what came of it.</summary>
public sealed class ShellWindowTests
{
    private readonly ShellTestHarness _h = new();
    private readonly FakeShellWindow _window = new();

    public ShellWindowTests()
    {
        _h.Shell.Window = _window;
    }

    private string Set(WindowRequest request) => ((IRavenShell)_h.Shell).SetWindow(request);

    [Fact]
    public void Minimize_minimizes_through_the_window_and_says_raven_still_listens()
    {
        Set(WindowRequest.Minimize).ShouldContain("still listening");

        _window.Calls.ShouldBe(["minimize"]);
        _window.State.ShouldBe(ShellWindowState.Minimized);
    }

    [Fact]
    public void Minimize_when_minimized_already_says_so_and_changes_nothing()
    {
        _window.State = ShellWindowState.Minimized;

        Set(WindowRequest.Minimize).ShouldBe("CodeSwitchX is already minimized.");

        _window.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void Maximize_from_minimized_maximizes_and_brings_it_to_the_front()
    {
        _window.State = ShellWindowState.Minimized;
        _window.IsInFront = false;

        Set(WindowRequest.Maximize).ShouldBe("CodeSwitchX is maximized.");

        _window.Calls.ShouldBe(["show Maximized"]);
        (_window.State, _window.IsInFront).ShouldBe((ShellWindowState.Maximized, true));
    }

    [Fact]
    public void Maximize_when_maximized_and_in_front_already_says_so()
    {
        _window.State = ShellWindowState.Maximized;

        Set(WindowRequest.Maximize).ShouldBe("CodeSwitchX is already maximized.");

        _window.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void Maximized_but_behind_another_window_is_brought_to_the_front()
    {
        _window.State = ShellWindowState.Maximized;
        _window.IsInFront = false;

        Set(WindowRequest.Maximize).ShouldBe("CodeSwitchX is maximized.");

        _window.Calls.ShouldBe(["show Maximized"]);
    }

    [Fact]
    public void Restore_brings_it_back_to_its_normal_size()
    {
        _window.State = ShellWindowState.Maximized;

        Set(WindowRequest.Restore).ShouldBe("CodeSwitchX is back.");

        _window.State.ShouldBe(ShellWindowState.Normal);
    }

    [Fact]
    public void A_window_windows_keeps_behind_is_said_honestly()
    {
        _window.State = ShellWindowState.Minimized;
        _window.IsInFront = false;
        _window.ForegroundRefused = true;

        Set(WindowRequest.Restore).ShouldContain("Windows kept another window in front");
    }

    [Fact]
    public void No_window_yet_is_said_not_thrown()
    {
        _h.Shell.Window = null;

        Set(WindowRequest.Maximize).ShouldContain("no window");
    }

    private sealed class FakeShellWindow : IShellWindow
    {
        public ShellWindowState State { get; set; } = ShellWindowState.Normal;

        public bool IsInFront { get; set; } = true;

        /// <summary>Windows' foreground lock: shown, but not in front.</summary>
        public bool ForegroundRefused { get; set; }

        public List<string> Calls { get; } = [];

        public void Minimize()
        {
            Calls.Add("minimize");
            State = ShellWindowState.Minimized;
            IsInFront = false;
        }

        public void Show(ShellWindowState state)
        {
            Calls.Add($"show {state}");
            State = state;
            IsInFront = !ForegroundRefused;
        }
    }
}
