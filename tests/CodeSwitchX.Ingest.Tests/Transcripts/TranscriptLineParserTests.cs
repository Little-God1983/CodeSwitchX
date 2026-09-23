using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Ingest.Transcripts;

namespace CodeSwitchX.Ingest.Tests.Transcripts;

public class TranscriptLineParserTests
{
    [Fact]
    public void Assistant_line_yields_usage_model_and_message_id()
    {
        const string line = """{"type":"assistant","sessionId":"s1","cwd":"C:\\Repo\\App","timestamp":"2026-09-23T10:00:05.000Z","uuid":"a1","message":{"id":"msg_1","model":"claude-sonnet-5","role":"assistant","content":[{"type":"tool_use","id":"tu1","name":"Bash","input":{"command":"ls"}}],"usage":{"input_tokens":100,"output_tokens":20,"cache_creation_input_tokens":500,"cache_read_input_tokens":3000,"service_tier":"standard"}}}""";

        var parsed = TranscriptLineParser.TryParse(line).ShouldBeOfType<AssistantLine>();

        parsed.SessionId.ShouldBe("s1");
        parsed.Cwd.ShouldBe(@"C:\Repo\App");
        parsed.Timestamp.ShouldBe(new DateTimeOffset(2026, 9, 23, 10, 0, 5, TimeSpan.Zero));
        parsed.MessageId.ShouldBe("msg_1");
        parsed.Model.ShouldBe("claude-sonnet-5");
        parsed.Usage.ShouldBe(new TokenUsage(100, 20, 500, 3000));
        parsed.HasToolUse.ShouldBeTrue();
    }

    [Fact]
    public void Assistant_line_without_usage_has_null_usage()
    {
        var parsed = TranscriptLineParser.TryParse("""{"type":"assistant","message":{"id":"m","content":[{"type":"text","text":"hi"}]}}""")
            .ShouldBeOfType<AssistantLine>();

        parsed.Usage.ShouldBeNull();
        parsed.HasToolUse.ShouldBeFalse();
    }

    [Fact]
    public void User_prompt_with_string_content_is_a_prompt()
    {
        var parsed = TranscriptLineParser.TryParse("""{"type":"user","sessionId":"s1","timestamp":"2026-09-23T10:00:00Z","message":{"role":"user","content":"Fix the build"}}""")
            .ShouldBeOfType<UserLine>();

        parsed.Text.ShouldBe("Fix the build");
        parsed.IsToolResult.ShouldBeFalse();
        parsed.IsMeta.ShouldBeFalse();
    }

    [Fact]
    public void User_text_block_is_a_prompt_and_tool_result_blocks_are_not()
    {
        TranscriptLineParser.TryParse("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"Hello there"}]}}""")
            .ShouldBeOfType<UserLine>().Text.ShouldBe("Hello there");

        var result = TranscriptLineParser.TryParse("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tu1","content":"ok"}]}}""")
            .ShouldBeOfType<UserLine>();
        result.IsToolResult.ShouldBeTrue();
        result.Text.ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"type":"user","isMeta":true,"message":{"role":"user","content":"Caveat: ..."}}""")]
    [InlineData("""{"type":"user","message":{"role":"user","content":"<command-name>/clear</command-name>"}}""")]
    [InlineData("""{"type":"user","message":{"role":"user","content":"<local-command-stdout>done</local-command-stdout>"}}""")]
    public void Meta_and_slash_command_lines_are_flagged_meta(string line)
    {
        TranscriptLineParser.TryParse(line).ShouldBeOfType<UserLine>().IsMeta.ShouldBeTrue();
    }

    [Fact]
    public void Summary_line_carries_the_title()
    {
        TranscriptLineParser.TryParse("""{"type":"summary","summary":"Fix build errors in App","leafUuid":"a1"}""")
            .ShouldBeOfType<SummaryLine>().Title.ShouldBe("Fix build errors in App");
    }

    [Theory]
    [InlineData("""{"type":"system","subtype":"init"}""", "system")]
    [InlineData("""{"type":"progress"}""", "progress")]
    [InlineData("""{"no":"type"}""", "")]
    public void Other_types_are_kept_as_other(string line, string type)
    {
        TranscriptLineParser.TryParse(line).ShouldBeOfType<OtherLine>().Type.ShouldBe(type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("[1,2]")]
    public void Garbage_returns_null(string line)
    {
        TranscriptLineParser.TryParse(line).ShouldBeNull();
    }
}
