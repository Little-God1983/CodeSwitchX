using CodeSwitchX.Ingest.Transcripts;

namespace CodeSwitchX.Ingest.Tests.Transcripts;

public sealed class TranscriptLastReplyTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "csx-last-" + Guid.NewGuid().ToString("N") + ".jsonl");

    public void Dispose() => File.Delete(_path);

    private static string Assistant(string text, bool sidechain = false) =>
        $$$"""{"type":"assistant","isSidechain":{{{(sidechain ? "true" : "false")}}},"message":{"role":"assistant","content":[{"type":"text","text":"{{{text}}}"}]}}""";

    private const string ToolUse =
        """{"type":"assistant","isSidechain":false,"message":{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"Bash","input":{}}]}}""";

    private const string ToolResult =
        """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":"ok"}]}}""";

    [Fact]
    public void The_last_reply_of_the_main_chat_is_read_past_tool_calls_and_subagents()
    {
        File.WriteAllLines(_path, [Assistant("First."), Assistant("The retry now backs off and the test passes."), ToolUse, ToolResult,
            Assistant("A subagent's words.", sidechain: true)]);

        TranscriptLastReply.Read(_path).ShouldBe("The retry now backs off and the test passes.");
    }

    [Fact]
    public void A_long_reply_keeps_its_end_cut_at_a_word()
    {
        File.WriteAllLines(_path, [Assistant("alpha beta gamma delta epsilon")]);

        TranscriptLastReply.Read(_path, maxChars: 15).ShouldBe("…delta epsilon");
    }

    [Fact]
    public void No_file_and_no_reply_are_null()
    {
        TranscriptLastReply.Read(_path).ShouldBeNull();
        TranscriptLastReply.Read(null).ShouldBeNull();
        File.WriteAllLines(_path, [ToolUse, ToolResult]);
        TranscriptLastReply.Read(_path).ShouldBeNull();
    }
}
