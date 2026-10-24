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

    // #301: a reply that ended in a table gave the teller "| Skipped | 580 rows, no measurable cost. |" to word.
    [Fact]
    public void A_reply_is_read_as_prose_without_its_tables_code_and_markdown()
    {
        File.WriteAllLines(_path, [Assistant(
            "## Review\\n\\n| # | Finding | Verdict |\\n|---|---|---|\\n| 1 | Capped list | Skipped |\\n\\n```powershell\\n.\\\\build.ps1\\n```\\n\\n"
            + "- All tests pass.\\n\\nTell me **which** to fix in `TileView.cs`, or say merge.")]);

        TranscriptLastReply.Read(_path).ShouldBe("Review. All tests pass. Tell me which to fix in TileView.cs, or say merge.");
    }

    // Review round 1 of #301: a line starting with an issue number lost its '#', rules and link targets were told.
    [Fact]
    public void Issue_numbers_rules_and_link_targets_are_read_as_prose_would_say_them()
    {
        File.WriteAllLines(_path, [Assistant(
            "#301 is fixed.\\n\\n---\\n\\n***\\n\\nOpened [PR #302](https://github.com/Little-God1983/CodeSwitchX/pull/302).")]);

        TranscriptLastReply.Read(_path).ShouldBe("#301 is fixed. Opened PR #302.");
    }

    [Fact]
    public void A_heading_and_list_items_end_as_sentences()
    {
        File.WriteAllLines(_path, [Assistant("## Done\\n- Fixed login\\n- Fixed logout\\n\\nAll green.")]);

        TranscriptLastReply.Read(_path).ShouldBe("Done. Fixed login. Fixed logout. All green.");
    }

    // Review round 1 of #301: inline triple backticks or a longer fence flipped the code flag, and the closing words were lost.
    [Fact]
    public void Inline_triple_backticks_and_longer_fences_leave_the_closing_words()
    {
        File.WriteAllLines(_path, [Assistant(
            "```npm test``` passes now.\\n\\n````md\\n```cs\\nx();\\n```\\n````\\n\\nShould I merge?")]);

        TranscriptLastReply.Read(_path).ShouldBe("npm test passes now. Should I merge?");
    }

    [Fact]
    public void A_reply_that_is_only_a_table_says_nothing_rather_than_an_earlier_reply()
    {
        File.WriteAllLines(_path, [Assistant("Now the tests."), ToolUse, ToolResult, Assistant("| a | b |\\n|---|---|\\n| 1 | 2 |")]);

        TranscriptLastReply.Read(_path).ShouldBeNull();
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
