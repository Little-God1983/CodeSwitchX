using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Hosting;
using CodeSwitchX.UI.Shell;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Shell;

/// <summary>Exit asks whether the VS Code windows close too (#420).</summary>
public class ExitTests
{
    private readonly ShellTestHarness _h = new();
    private readonly ClosingWindow _window = new();
    private readonly List<(string Question, string Detail)> _asked = [];
    private ExitChoice _answer = ExitChoice.Cancel;

    public ExitTests()
    {
        _h.Shell.Window = _window;
        _h.Shell.AskBeforeExit = (question, detail) =>
        {
            _asked.Add((question, detail));
            return _answer;
        };
    }

    [Theory]
    [InlineData(3, 2, "3 VS Code windows are open, and 2 of them have a chat that is still working.")]
    [InlineData(2, 1, "2 VS Code windows are open, and 1 of them has a chat that is still working.")]
    [InlineData(1, 1, "1 VS Code window is open, and a chat in it is still working.")]
    [InlineData(2, 0, "2 VS Code windows are open.")]
    [InlineData(1, 0, "1 VS Code window is open.")]
    public void The_question_counts_the_open_windows_and_those_still_working(int open, int working, string question) =>
        ExitQuestion.Question(open, working).ShouldBe(question);

    [Fact]
    public void The_detail_warns_about_cut_off_chats_only_when_one_works()
    {
        ExitQuestion.Detail(1).ShouldBe("Closing them stops those chats mid-turn. VS Code still asks about unsaved files.");
        ExitQuestion.Detail(0).ShouldBe("VS Code still asks about unsaved files.");
    }

    private async Task AppIsOpenAsync()
    {
        _h.VsCodeWindowAppears();
        await _h.Shell.InitializeAsync(CancellationToken.None);
        await _h.Shell.EnterCabAsync(_h.App.Id);
        _h.Host.Get(_h.App.Id)!.State.ShouldBe(HostState.Running);
    }

    private void AppChatWorks()
    {
        var at = DateTimeOffset.UtcNow;
        _h.Bus.Publish(new SessionChanged(null, new SessionSnapshot
        {
            SessionId = "s1", WorkspaceId = _h.App.Id, State = SessionState.Working, StartedAt = at, LastEventAt = at, StateSince = at, Title = "T",
            Version = 1, HookSeen = true,
        }));
    }

    [Fact]
    public async Task With_no_vs_code_window_open_Exit_closes_without_asking()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);

        _h.Shell.ExitCommand.Execute(null);

        _asked.ShouldBeEmpty();
        _window.Closed.ShouldBeTrue();
    }

    [Fact]
    public async Task Exit_asks_with_the_open_and_working_windows()
    {
        await AppIsOpenAsync();
        AppChatWorks();

        _h.Shell.ExitCommand.Execute(null);

        _asked.ShouldBe([("1 VS Code window is open, and a chat in it is still working.",
            "Closing them stops those chats mid-turn. VS Code still asks about unsaved files.")]);
    }

    [Fact]
    public async Task Close_all_asks_the_vs_code_windows_to_close_and_closes_CodeSwitchX()
    {
        await AppIsOpenAsync();
        _answer = ExitChoice.CloseAll;

        _h.Shell.ExitCommand.Execute(null);

        _h.Docker.Received(1).Close(500);
        _window.Closed.ShouldBeTrue();
    }

    [Fact]
    public async Task Close_only_CodeSwitchX_leaves_vs_code_open()
    {
        await AppIsOpenAsync();
        _answer = ExitChoice.CloseOnly;

        _h.Shell.ExitCommand.Execute(null);

        _h.Docker.DidNotReceive().Close(Arg.Any<nint>());
        _window.Closed.ShouldBeTrue();
    }

    [Fact]
    public async Task Cancel_closes_nothing()
    {
        await AppIsOpenAsync();

        _h.Shell.ExitCommand.Execute(null);

        _h.Docker.DidNotReceive().Close(Arg.Any<nint>());
        _window.Closed.ShouldBeFalse();
    }

    [Fact]
    public async Task Without_a_window_to_ask_in_nothing_closes()
    {
        await AppIsOpenAsync();
        _h.Shell.AskBeforeExit = null;

        _h.Shell.ExitCommand.Execute(null);

        _h.Docker.DidNotReceive().Close(Arg.Any<nint>());
        _window.Closed.ShouldBeFalse();
    }

    [Fact]
    public async Task A_window_the_user_closed_already_is_not_counted()
    {
        await AppIsOpenAsync();
        _h.Docker.IsAlive(500).Returns(false);

        _h.Shell.ExitCommand.Execute(null);

        _asked.ShouldBeEmpty();
        _window.Closed.ShouldBeTrue();
    }

    [Fact]
    public async Task Without_a_window_to_close_nothing_is_asked_and_no_vs_code_closes()
    {
        await AppIsOpenAsync();
        _h.Shell.Window = null;
        _answer = ExitChoice.CloseAll;

        _h.Shell.ExitCommand.Execute(null);

        _asked.ShouldBeEmpty();
        _h.Docker.DidNotReceive().Close(Arg.Any<nint>());
    }

    [Fact]
    public async Task A_second_Exit_while_the_question_is_open_asks_nothing_more()
    {
        await AppIsOpenAsync();
        _h.Shell.AskBeforeExit = (question, detail) =>
        {
            _asked.Add((question, detail));
            _h.Shell.ExitCommand.Execute(null);
            return ExitChoice.Cancel;
        };

        _h.Shell.ExitCommand.Execute(null);

        _asked.Count.ShouldBe(1);
        _window.Closed.ShouldBeFalse();
    }

    [Fact]
    public async Task After_a_cancelled_question_Exit_asks_again()
    {
        await AppIsOpenAsync();
        _h.Shell.ExitCommand.Execute(null);
        _answer = ExitChoice.CloseOnly;

        _h.Shell.ExitCommand.Execute(null);

        _asked.Count.ShouldBe(2);
        _window.Closed.ShouldBeTrue();
    }

    private sealed class ClosingWindow : IShellWindow
    {
        public bool Closed { get; private set; }
        public ShellWindowState State => ShellWindowState.Normal;
        public ShellWindowState Restored => ShellWindowState.Normal;
        public bool IsInFront => true;
        public nint Dialog => 0;

        public void Minimize()
        {
        }

        public void Show(ShellWindowState state)
        {
        }

        public void Close() => Closed = true;
    }
}
