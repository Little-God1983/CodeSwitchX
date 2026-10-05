using CodeSwitchX.Core.Sessions;
using CodeSwitchX.UI.Shell;
using NSubstitute;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Shell;

/// <summary>#135: a workspace whose Raven chat has open cards is not removed without asking what becomes of them.</summary>
public sealed class RemoveWorkspaceTests
{
    private readonly ShellTestHarness _h = new();
    private readonly List<(string Workspace, int Cards)> _asked = [];
    private RemoveChoice _choice = RemoveChoice.Cancel;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task InitAsync()
    {
        _h.App.Number = 1; // it has a Raven chat
        _h.Shell.AskBeforeRemove = (workspace, cards) =>
        {
            _asked.Add((workspace, cards));
            return Task.FromResult(_choice);
        };
        await _h.Shell.InitializeAsync(CancellationToken.None);
    }

    /// <summary>A permission prompt of a chat in App, held for Raven's panel.</summary>
    private Task<ChatAskClosed?> CardInApp(string id = "p1")
    {
        _h.YardDirectory.Show("s1", "App", "Fix the upload");
        return _h.Asks.HoldAsync(new ChatAsk(id,
            new HookEvent { SessionId = "s1", EventName = "PermissionRequest", At = _h.Time.GetUtcNow(), ToolName = "Bash", ToolInputHash = id },
            [], new ChatPermission("Bash", "run a command", "npm test", null)), CancellationToken.None);
    }

    private async Task<bool> WasRemovedAsync()
    {
        try
        {
            await _h.Workspaces.Received().RemoveAsync(_h.App.Id, Arg.Any<CancellationToken>());
            return true;
        }
        catch (NSubstitute.Exceptions.ReceivedCallsException)
        {
            return false;
        }
    }

    [Fact]
    public async Task Without_open_cards_removing_asks_nothing()
    {
        await InitAsync();

        await _h.Shell.Yard.UnregisterAsync(_h.App.Id);

        _asked.ShouldBeEmpty();
        (await WasRemovedAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task With_an_open_card_the_user_is_asked_with_the_window_and_how_many_wait()
    {
        await InitAsync();
        _ = CardInApp();

        await _h.Shell.Yard.UnregisterAsync(_h.App.Id);

        _asked.ShouldBe([("App", 1)]);
    }

    [Fact]
    public async Task Answer_them_first_removes_nothing_and_shows_the_window_s_chat()
    {
        await InitAsync();
        var held = CardInApp();
        _choice = RemoveChoice.AnswerFirst;

        await _h.Shell.Yard.UnregisterAsync(_h.App.Id);

        (await WasRemovedAsync()).ShouldBeFalse();
        _h.Shell.Raven.SelectedChat.WorkspaceId.ShouldBe(_h.App.Id);
        held.IsCompleted.ShouldBeFalse("the card still waits for the user");
    }

    [Fact]
    public async Task Leave_them_to_vs_code_leaves_each_card_there_then_removes()
    {
        await InitAsync();
        var held = CardInApp();
        _choice = RemoveChoice.LeaveToVsCode;

        await _h.Shell.Yard.UnregisterAsync(_h.App.Id);

        await held.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        _h.Shell.Raven.Log.Single(e => e.Ask is not null).Ask!.Outcome.ShouldBe("Left to VS Code: answer it in the chat's tab.");
        (await WasRemovedAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task Cancel_does_nothing()
    {
        await InitAsync();
        var held = CardInApp();
        _choice = RemoveChoice.Cancel;

        await _h.Shell.Yard.UnregisterAsync(_h.App.Id);

        (await WasRemovedAsync()).ShouldBeFalse();
        held.IsCompleted.ShouldBeFalse();
    }
}
