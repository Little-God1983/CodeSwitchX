using System.Reflection;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Ingest.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CodeSwitchX.Ingest.Tests.Mcp;

/// <summary>
/// A message from another Claude session starts a turn of a Raven chat's brain, with all its tools (#193). What it asks is
/// no word of the user's, so every tool that acts refuses a Raven chat that is not in its user's question; the looking
/// tools stay open, and a caller that is no Raven chat is let be.
/// </summary>
public sealed class AskedOnlyTests
{
    private static readonly ChatScope InCodeSwitchX = new(FakeYard.CodeSwitchXId);
    private readonly FakeYard _yard = new();
    private readonly FakeActions _actions = new();
    private readonly AskedChats _asked = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stop_it_is_refused_in_a_turn_of_the_brain_s_own_and_done_in_the_user_s_question()
    {
        var tools = new YardActionTools(_yard, _actions, scope: InCodeSwitchX, asked: _asked);

        (await Should.ThrowAsync<McpException>(() => tools.StopChat(cancellationToken: Ct))).Message.ShouldBe(ToolActs.NotAsked);
        _actions.Stopped.ShouldBeNull("a chat's message stops nothing");

        _asked.Begin(InCodeSwitchX.Key!);
        (await tools.StopChat(cancellationToken: Ct)).ShouldBe("stopped");
        _actions.Stopped.ShouldNotBeNull().Title.ShouldBe("Speech gate");
    }

    [Fact]
    public async Task Every_tool_that_acts_is_refused_outside_the_user_s_question()
    {
        // Every tool of the app's not marked read-only, found by its attribute: one added later is refused too, or this fails.
        var acting = typeof(YardActionTools).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods().Select(method => (type, method, tool: method.GetCustomAttribute<McpServerToolAttribute>())))
            .Where(m => m.tool is { ReadOnly: false })
            .ToList();
        acting.Select(m => m.type).Distinct().ShouldBe([typeof(YardActionTools), typeof(SettingsTools)], ignoreOrder: true,
            "a tool type that acts is checked here: give it the chat scope and AskedChats, and AskedOnly first in each tool");
        acting.Select(m => m.tool!.Name).ShouldContain("answer_permission");
        acting.Select(m => m.tool!.Name).ShouldContain("set_setting");

        foreach (var (type, method, tool) in acting)
        {
            object instance = type == typeof(YardActionTools)
                ? new YardActionTools(_yard, _actions, scope: InCodeSwitchX, asked: _asked)
                : new SettingsTools(null!, InCodeSwitchX, _asked); // refused before the settings are touched
            var arguments = method.GetParameters()
                .Select(p => p.ParameterType == typeof(CancellationToken) ? Ct : p.HasDefaultValue ? p.DefaultValue : p.ParameterType == typeof(string) ? "x" : null)
                .ToArray();
            Exception? error = null;
            try
            {
                if (method.Invoke(instance, arguments) is Task task)
                {
                    await task;
                }
            }
            catch (TargetInvocationException ex)
            {
                error = ex.InnerException;
            }
            catch (McpException ex)
            {
                error = ex;
            }

            error.ShouldBeOfType<McpException>(tool!.Name).Message.ShouldBe(ToolActs.NotAsked, tool.Name);
        }

        _actions.Started.ShouldBeNull();
        _actions.Stopped.ShouldBeNull();
    }

    [Fact]
    public async Task Chat_0_acts_in_its_user_s_question_too()
    {
        var tools = new YardActionTools(_yard, _actions, scope: ChatScope.Yard, asked: _asked);
        await Should.ThrowAsync<McpException>(() => tools.SetDefaults("Opus", cancellationToken: Ct));

        _asked.Begin(YardMcp.OverviewChat);
        await tools.SetDefaults("Opus", cancellationToken: Ct);
    }

    [Fact]
    public async Task A_chat_header_that_names_no_Raven_chat_is_refused()
    {
        var tools = new YardActionTools(_yard, _actions, scope: new ChatScope(null, Unknown: true), asked: _asked);

        (await Should.ThrowAsync<McpException>(() => tools.StopChat("aaaaaaaa", Ct))).Message.ShouldBe(ToolActs.NotAsked);
    }

    [Fact]
    public async Task A_caller_that_is_no_Raven_chat_is_let_be()
    {
        // It sends no chat header: no brain of Raven's says when it is in a question.
        await new YardActionTools(_yard, _actions, scope: ChatScope.None, asked: _asked).StopChat("aaaaaaaa", Ct);

        _actions.Stopped.ShouldNotBeNull();
    }

    [Fact]
    public async Task Looking_stays_open()
    {
        (await new YardTools(_yard, scope: InCodeSwitchX).ListChats(cancellationToken: Ct)).ShouldNotBeEmpty();
    }

    [Fact]
    public void A_chat_is_asked_until_as_many_ends_as_begins()
    {
        var chat = InCodeSwitchX.Key!;
        _asked.Begin(chat);
        _asked.Begin(chat);
        _asked.End(chat);
        _asked.IsAsked(chat).ShouldBeTrue();
        _asked.IsAsked(chat.ToUpperInvariant()).ShouldBeTrue("the header is a guid, whatever its case");
        _asked.End(chat);
        _asked.IsAsked(chat).ShouldBeFalse();
        _asked.End(chat); // one too many is nothing
        _asked.IsAsked(chat).ShouldBeFalse();
    }
}
