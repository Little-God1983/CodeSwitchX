using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Ingest.Mcp;
using ModelContextProtocol;

namespace CodeSwitchX.Ingest.Tests.Mcp;

/// <summary>#297: Raven closes every idle chat of one window, and leaves the ones that work or wait for the user.</summary>
public sealed class CloseIdleChatsTests
{
    private readonly FakeYard _yard = new();
    private readonly FakeActions _actions = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private YardActionTools Tools(ChatScope? scope = null) => new(_yard, _actions, scope: scope);

    private void Chat(string id, string title, SessionState state, bool hasTab = true, bool needsYou = false) =>
        _yard.Chats.Add(_yard.Chats[2] with { Id = id, Title = title, State = state, NeedsYou = needsYou, HasTab = hasTab });

    public CloseIdleChatsTests()
    {
        // Diffusion-Full: Installer icons (idle, from FakeYard), and these.
        Chat("dddddddd-0004", "Package update", SessionState.Idle);
        Chat("eeeeeeee-0005", "Engine uninstall", SessionState.Ended); // a tab VS Code brought back, not running
        Chat("ffffffff-0006", "NuGet check", SessionState.Stale);
        Chat("99999999-0007", "Build fix", SessionState.Working);
        Chat("88888888-0008", "LoRA download", SessionState.Waiting, needsYou: true);
        Chat("77777777-0009", "Old opinion", SessionState.Ended, hasTab: false); // closed already, kept greyed
    }

    [Fact]
    public async Task The_idle_chats_of_the_named_window_are_closed_and_the_busy_ones_left()
    {
        var said = await Tools().CloseIdleChats("Diffusion Full", Ct);

        _actions.ClosedAll.Select(c => c.Title).ShouldBe(["Installer icons", "Package update", "Engine uninstall", "NuGet check"]);
        said.ShouldStartWith("Closed 4 idle chats in Diffusion-Full: Installer icons, Package update, Engine uninstall, NuGet check.");
        said.ShouldContain("Left open: Build fix (still working), LoRA download (waiting for the user).");
        said.ShouldNotContain("Old opinion");
    }

    [Fact]
    public async Task Asked_in_a_window_s_chat_with_no_workspace_it_closes_that_window_s_idle_chats()
    {
        await Tools(new ChatScope(FakeYard.DiffusionId)).CloseIdleChats(cancellationToken: Ct);

        _actions.ClosedAll.Count.ShouldBe(4);
        _actions.ClosedAll.ShouldAllBe(c => c.WorkspaceId == FakeYard.DiffusionId);
        _actions.Acted.ShouldContain((new ChatScope(FakeYard.DiffusionId).Key, FakeYard.DiffusionId, "close_idle_chats"));
    }

    [Fact]
    public async Task Asked_in_the_Yard_s_chat_with_no_workspace_it_closes_nothing_and_asks_which()
    {
        var error = await Should.ThrowAsync<McpException>(() => Tools(ChatScope.Yard).CloseIdleChats(cancellationToken: Ct));

        error.Message.ShouldContain("Say which window");
        _actions.ClosedAll.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_window_with_no_idle_chat_closes_nothing_and_says_so()
    {
        var said = await Tools().CloseIdleChats("CodeSwitchX", Ct);

        said.ShouldBe("No idle chat in CodeSwitchX to close. Left open: Speech gate (still working), Raven brain (waiting for the user).");
        _actions.ClosedAll.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_chat_that_cannot_be_closed_is_told_and_the_others_are_closed()
    {
        _actions.CloseRefusals["dddddddd-0004"] = "VS Code did not close the chat: VS Code did not close the tab.";

        var said = await Tools().CloseIdleChats("Diffusion Full", Ct);

        said.ShouldStartWith("Closed 3 idle chats in Diffusion-Full: Installer icons, Engine uninstall, NuGet check.");
        said.ShouldContain("Not closed: Package update (VS Code did not close the chat: VS Code did not close the tab).", Case.Sensitive,
            "one period, not \".).\"");
    }

    /// <summary>Review of #297: one running in a terminal is no chat of the window's tabs, and no failure to close.</summary>
    [Fact]
    public async Task An_idle_chat_outside_VS_Code_s_tabs_is_left_open_and_said_to_run_elsewhere()
    {
        _actions.OutsideTabs.Add("dddddddd-0004");

        var said = await Tools().CloseIdleChats("Diffusion Full", Ct);

        said.ShouldNotContain("Not closed");
        said.ShouldContain("Package update (not in a VS Code tab)");
    }

    /// <summary>Review of #297: the closes run one after another, and a chat that starts a turn meanwhile is not cut off.</summary>
    [Fact]
    public async Task A_chat_that_starts_working_while_the_others_close_is_left_open()
    {
        _actions.OnClose = closing =>
        {
            if (closing.Id == "cccccccc-0003")
            {
                var i = _yard.Chats.FindIndex(c => c.Id == "eeeeeeee-0005");
                _yard.Chats[i] = _yard.Chats[i] with { State = SessionState.Working };
            }
        };

        var said = await Tools().CloseIdleChats("Diffusion Full", Ct);

        _actions.ClosedAll.Select(c => c.Title).ShouldBe(["Installer icons", "Package update", "NuGet check"]);
        said.ShouldContain("Engine uninstall (still working)");
    }

    /// <summary>Review of #297: with the window's VS Code closed every tab fails alike, and Raven says the reason once.</summary>
    [Fact]
    public async Task Chats_not_closed_for_the_same_reason_are_named_with_it_once()
    {
        const string closed = "Diffusion-Full's VS Code is not open: it brings the chat's tab back when it opens, and it can be closed then.";
        foreach (var id in new[] { "cccccccc-0003", "dddddddd-0004", "eeeeeeee-0005", "ffffffff-0006" })
        {
            _actions.CloseRefusals[id] = closed;
        }

        var said = await Tools().CloseIdleChats("Diffusion Full", Ct);

        said.ShouldStartWith("No idle chat in Diffusion-Full could be closed. Not closed: Installer icons, Package update, Engine uninstall, NuGet check "
            + "(Diffusion-Full's VS Code is not open: it brings the chat's tab back when it opens, and it can be closed then).");
    }

    [Fact]
    public async Task One_idle_chat_is_one_chat()
    {
        _yard.Chats.RemoveAll(c => c.WorkspaceId == FakeYard.DiffusionId && c.Id != "cccccccc-0003");

        (await Tools().CloseIdleChats("Diffusion Full", Ct)).ShouldBe("Closed 1 idle chat in Diffusion-Full: Installer icons.");
    }
}
