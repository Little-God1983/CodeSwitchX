namespace CodeSwitchX.Conductor.Tests;

public sealed class ClaudeStreamTests
{
    [Fact]
    public void A_text_delta_is_a_piece_of_the_reply()
    {
        ClaudeStream.Read(StreamJson.Text("Hey")).ShouldBeOfType<ClaudeEvents>().Events.ShouldBe([new BrainText("Hey")]);
    }

    [Fact]
    public void Thinking_and_the_whole_assistant_text_are_no_news()
    {
        // The reply already came in pieces; the assistant message repeats it whole.
        ClaudeStream.Read(StreamJson.Thinking).ShouldBeNull();
        ClaudeStream.Read(StreamJson.AssistantText("Hey")).ShouldBeNull();
    }

    [Fact]
    public void A_tool_use_is_a_call_under_the_tool_s_own_name()
    {
        var line = ClaudeStream.Read(StreamJson.ToolUse("t1", "mcp__codeswitchx__list_chats", """{"filter":"needs_me"}""")).ShouldBeOfType<ClaudeEvents>();

        line.Events.ShouldHaveSingleItem().ShouldBe(new BrainToolCall("t1", "list_chats", """{"filter":"needs_me"}"""));
    }

    [Fact]
    public void A_tool_result_says_whether_it_failed()
    {
        ClaudeStream.Read(StreamJson.ToolResult("t1")).ShouldBeOfType<ClaudeEvents>().Events.ShouldBe([new BrainToolResult("t1", false)]);
        ClaudeStream.Read(StreamJson.ToolResult("t2", error: true)).ShouldBeOfType<ClaudeEvents>().Events.ShouldBe([new BrainToolResult("t2", true)]);
    }

    [Fact]
    public void A_subagent_s_lines_are_no_news()
    {
        ClaudeStream.Read(StreamJson.Text("inner", parent: "toolu_1")).ShouldBeNull();
    }

    [Fact]
    public void Init_names_the_MCP_servers_and_whether_they_connected()
    {
        var init = ClaudeStream.Read(StreamJson.Init("failed")).ShouldBeOfType<ClaudeInit>();

        init.Model.ShouldBe("claude-haiku-4-5-20251001");
        init.McpServers["codeswitchx"].ShouldBe("failed");
    }

    [Fact]
    public void A_result_ends_the_turn()
    {
        ClaudeStream.Read(StreamJson.Result()).ShouldBe(new ClaudeTurnOver(null));
    }

    [Fact]
    public void A_failed_result_says_why()
    {
        ClaudeStream.Read(StreamJson.ErrorResult).ShouldBe(new ClaudeTurnOver("API Error: 529 Overloaded"));
        ClaudeStream.Read("""{"type":"result","subtype":"error_during_execution","is_error":false,"errors":["Stream closed"]}""")
            .ShouldBe(new ClaudeTurnOver("Stream closed"));
        ClaudeStream.Read("""{"type":"result","subtype":"error_max_turns","is_error":true}""").ShouldBe(new ClaudeTurnOver("error_max_turns"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"type":"rate_limit_event"}""")]
    [InlineData("""{"type":"system","subtype":"status"}""")]
    [InlineData("""{"type":"assistant","message":"odd"}""")]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":""}},"parent_tool_use_id":null}""")]
    public void Anything_else_is_no_news(string line)
    {
        ClaudeStream.Read(line).ShouldBeNull();
    }

    [Theory]
    [InlineData("mcp__codeswitchx__list_chats", "list_chats")]
    [InlineData("mcp__other__get__thing", "get__thing")]
    [InlineData("Read", "Read")]
    [InlineData("mcp__broken", "mcp__broken")]
    public void The_provider_s_prefix_comes_off_a_tool_name(string name, string tool)
    {
        ClaudeStream.ToolName(name).ShouldBe(tool);
    }
}
