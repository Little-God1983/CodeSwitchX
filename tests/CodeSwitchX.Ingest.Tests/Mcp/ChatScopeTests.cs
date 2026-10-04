using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Ingest.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace CodeSwitchX.Ingest.Tests.Mcp;

/// <summary>
/// A window's Raven chat (#123): its brain's tools act on that window when it names none, so "stop it" in chat 1 stops the
/// chat working in CodeSwitchX, not one in another window. The Yard's chat names no window.
/// </summary>
public sealed class ChatScopeTests
{
    private static readonly ChatScope InCodeSwitchX = new(FakeYard.CodeSwitchXId);
    private static readonly ChatScope InDiffusion = new(FakeYard.DiffusionId);
    private readonly FakeYard _yard = new();
    private readonly FakeActions _actions = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_scope_is_read_from_the_chat_header()
    {
        var context = new DefaultHttpContext();
        ChatScope.Of(context).ShouldBe(ChatScope.None);
        ChatScope.Of(null).ShouldBe(ChatScope.None);

        context.Request.Headers[YardMcp.ChatHeader] = FakeYard.DiffusionId.ToString("D");
        ChatScope.Of(context).WorkspaceId.ShouldBe(FakeYard.DiffusionId);

        context.Request.Headers[YardMcp.ChatHeader] = "not a window";
        ChatScope.Of(context).ShouldBe(ChatScope.None);
    }

    [Fact]
    public async Task List_chats_lists_the_window_s_chats_unless_another_or_all_is_named()
    {
        var tools = new YardTools(_yard, scope: InDiffusion);

        (await tools.ListChats(cancellationToken: Ct)).Select(c => c.Title).ShouldBe(["Installer icons"]);
        (await tools.ListChats(workspace: "CodeSwitchX", cancellationToken: Ct)).Select(c => c.Title).ShouldBe(["Speech gate", "Raven brain"]);
        (await tools.ListChats(workspace: "all", cancellationToken: Ct)).Count.ShouldBe(3);
        (await new YardTools(_yard, scope: ChatScope.None).ListChats(cancellationToken: Ct)).Count.ShouldBe(3, "the Yard's chat sees every window");
    }

    [Fact]
    public async Task Stop_it_in_a_window_s_chat_stops_the_chat_working_there()
    {
        (await new YardActionTools(_yard, _actions, scope: InCodeSwitchX).StopChat(cancellationToken: Ct)).ShouldBe("stopped");

        _actions.Stopped.ShouldNotBeNull().Title.ShouldBe("Speech gate");
    }

    [Fact]
    public async Task Stop_it_where_no_chat_works_stops_nothing_and_does_not_reach_into_another_window()
    {
        var error = await Should.ThrowAsync<McpException>(() => new YardActionTools(_yard, _actions, scope: InDiffusion).StopChat(cancellationToken: Ct));

        error.Message.ShouldStartWith("No chat in Diffusion-Full is working");
        _actions.Calls.ShouldBeEmpty("Speech gate works in another window");
    }

    [Fact]
    public async Task Stop_it_with_two_chats_working_in_the_window_asks_which()
    {
        _yard.Chats.Add(_yard.Chats[0] with { Id = "eeeeeeee-0005", Title = "Docs pass" });

        var error = await Should.ThrowAsync<McpException>(() => new YardActionTools(_yard, _actions, scope: InCodeSwitchX).StopChat(cancellationToken: Ct));

        error.Message.ShouldContain("Speech gate (id aaaaaaaa)");
        error.Message.ShouldContain("Docs pass (id eeeeeeee)");
        error.Message.ShouldContain("Ask the user which one");
        _actions.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task In_the_yard_s_chat_a_chat_must_be_named()
    {
        var error = await Should.ThrowAsync<McpException>(() => new YardActionTools(_yard, _actions, scope: ChatScope.None).StopChat(cancellationToken: Ct));

        error.Message.ShouldStartWith("Say which chat to stop");
    }

    [Fact]
    public async Task A_chat_of_another_window_named_by_its_id_is_still_acted_on()
    {
        await new YardActionTools(_yard, _actions, scope: InDiffusion).StopChat("aaaaaaaa", Ct);

        _actions.Stopped.ShouldNotBeNull().Title.ShouldBe("Speech gate");
    }

    [Fact]
    public async Task Open_it_and_start_a_chat_mean_the_window_when_none_is_named()
    {
        var tools = new YardActionTools(_yard, _actions, scope: InDiffusion);

        await tools.OpenWorkspace(cancellationToken: Ct);
        _actions.Opened.ShouldNotBeNull().Name.ShouldBe("Diffusion-Full");

        await tools.StartChat(cancellationToken: Ct);
        var started = _actions.Started.ShouldNotBeNull();
        started.Workspace.Name.ShouldBe("Diffusion-Full");
        started.Folder.ShouldBeNull("the window's own first folder");

        await tools.StartChat(folder: "Diffusion Nexus", cancellationToken: Ct);
        _actions.Started!.Value.Folder!.Path.ShouldBe(@"E:\Repos\DiffusionNexus");
    }

    [Fact]
    public async Task Starting_a_chat_in_the_yard_s_chat_needs_a_workspace()
    {
        var error = await Should.ThrowAsync<McpException>(() => new YardActionTools(_yard, _actions).StartChat(cancellationToken: Ct));

        error.Message.ShouldContain("Say in which workspace");
        _actions.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_window_gone_from_the_yard_is_said_so_not_that_the_user_is_in_the_yard()
    {
        var tools = new YardActionTools(_yard, _actions, scope: new ChatScope(Guid.NewGuid()));

        (await Should.ThrowAsync<McpException>(() => tools.StartChat(cancellationToken: Ct))).Message.ShouldContain("not on the Yard any more");
        (await Should.ThrowAsync<McpException>(() => tools.StopChat(cancellationToken: Ct))).Message.ShouldContain("not on the Yard any more");
        (await Should.ThrowAsync<McpException>(() => tools.OpenWorkspace(cancellationToken: Ct))).Message.ShouldContain("not on the Yard any more");
    }

    [Fact]
    public async Task List_chats_of_a_window_gone_from_the_yard_says_so_rather_than_nothing()
    {
        var error = await Should.ThrowAsync<McpException>(() => new YardTools(_yard, scope: new ChatScope(Guid.NewGuid())).ListChats(cancellationToken: Ct));

        error.Message.ShouldContain("not on the Yard any more");
    }

    [Fact]
    public async Task An_ask_id_picks_the_prompt_though_two_chats_in_the_window_ask()
    {
        var asks = new ChatAsks(new EventBus(NullLogger<EventBus>.Instance), TimeProvider.System) { Takes = _ => true };
        var first = asks.HoldAsync(new ChatAsk("p1",
            new HookEvent { SessionId = "aaaaaaaa-0001", EventName = "PermissionRequest", At = DateTimeOffset.UtcNow, ToolName = "Bash" },
            [], new ChatPermission("Bash", "run a command", "npm test", null)), CancellationToken.None);
        var second = asks.HoldAsync(new ChatAsk("q9",
            new HookEvent { SessionId = "bbbbbbbb-0002", EventName = "PermissionRequest", At = DateTimeOffset.UtcNow, ToolName = "Bash" },
            [], new ChatPermission("Bash", "run a command", "rm -rf build", null)), CancellationToken.None);

        (await new YardActionTools(_yard, _actions, asks, InCodeSwitchX).AnswerPermission("deny", ask: "q9", cancellationToken: Ct))
            .ShouldStartWith("Denied. The Raven brain chat");
        (await new YardActionTools(_yard, _actions, asks, ChatScope.None).AnswerPermission("deny", ask: "p1", cancellationToken: Ct))
            .ShouldStartWith("Denied. The Speech gate chat");

        (await first).ShouldNotBeNull();
        (await second).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_question_and_a_permission_prompt_are_answered_in_the_window_without_naming_the_chat()
    {
        var asks = new ChatAsks(new EventBus(NullLogger<EventBus>.Instance), TimeProvider.System) { Takes = _ => true };
        var question = asks.HoldAsync(new ChatAsk("q1",
            new HookEvent { SessionId = "bbbbbbbb-0002", EventName = "PreToolUse", At = DateTimeOffset.UtcNow, ToolName = "AskUserQuestion" },
            [new ChatQuestion("Which fruit?", null, [new ChatQuestionOption("Apple", null), new ChatQuestionOption("Banana", null)], false)]),
            CancellationToken.None);
        var prompt = asks.HoldAsync(new ChatAsk("p1",
            new HookEvent { SessionId = "aaaaaaaa-0001", EventName = "PermissionRequest", At = DateTimeOffset.UtcNow, ToolName = "Bash" },
            [], new ChatPermission("Bash", "run a command", "npm test", null)), CancellationToken.None);
        var tools = new YardActionTools(_yard, _actions, asks, InCodeSwitchX);

        (await tools.AnswerQuestion(["banana"], cancellationToken: Ct)).ShouldContain("The Raven brain chat has its answer (Banana)");
        (await tools.AnswerPermission("deny", cancellationToken: Ct)).ShouldStartWith("Denied. The Speech gate chat");

        (await question).ShouldNotBeNull();
        (await prompt).ShouldNotBeNull();
        var none = await Should.ThrowAsync<McpException>(() => new YardActionTools(_yard, _actions, asks, InDiffusion).AnswerQuestion(["Apple"], cancellationToken: Ct));
        none.Message.ShouldStartWith("No chat in Diffusion-Full asks a question");
    }
}
