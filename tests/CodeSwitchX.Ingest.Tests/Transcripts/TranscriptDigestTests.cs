using CodeSwitchX.Ingest.Transcripts;

namespace CodeSwitchX.Ingest.Tests.Transcripts;

public sealed class TranscriptDigestTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "csx-digest-" + Guid.NewGuid().ToString("N") + ".jsonl");

    public void Dispose() => File.Delete(_path);

    private static string User(string text, string extra = "") =>
        $$$"""{"type":"user","isSidechain":false{{{extra}}},"message":{"role":"user","content":{{{System.Text.Json.JsonSerializer.Serialize(text)}}}}}""";

    private static string Assistant(string text, bool sidechain = false) =>
        $$$"""{"type":"assistant","isSidechain":{{{(sidechain ? "true" : "false")}}},"message":{"role":"assistant","content":[{"type":"text","text":{{{System.Text.Json.JsonSerializer.Serialize(text)}}}}]}}""";

    private static string Tool(string name, string input) =>
        $$$"""{"type":"assistant","isSidechain":false,"message":{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"{{{name}}}","input":{{{input}}}}]}}""";

    private const string ToolResult =
        """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":"huge output"}]}}""";

    [Fact]
    public void The_conversation_is_told_in_steps_oldest_first()
    {
        File.WriteAllLines(_path, [
            User("Fix the upload retry."),
            Assistant("I will look at the uploader."),
            Tool("Edit", """{"file_path":"src/Upload.cs","old_string":"a","new_string":"b"}"""),
            ToolResult,
            Tool("Bash", """{"command":"dotnet test"}"""),
            Tool("Grep", """{"pattern":"Retry"}"""),
            Tool("SendMessage", """{"to":"raven-1","message":"Fixed; a fourth case waits for you."}"""),
            Assistant("A sub-agent's words.", sidechain: true),
            User("<command-name>/rename</command-name>"),
            User("meta", ""","isMeta":true"""),
            Assistant("The retry backs off now, and the tests pass."),
        ]);

        TranscriptDigest.Read(_path).ShouldBe("""
            User: Fix the upload retry.
            Claude: I will look at the uploader.
            Changed a file: src/Upload.cs
            Ran: dotnet test
            Used Grep: Retry
            Answered through a message: Fixed; a fourth case waits for you.
            Claude: The retry backs off now, and the tests pass.
            """.Replace("\r\n", "\n"));
    }

    [Fact]
    public void A_task_from_Raven_and_a_compaction_s_summary_say_what_they_are()
    {
        File.WriteAllLines(_path, [
            User("Another Claude session sent a message:\n<cross-session-message from=\"uds:\\\\.\\pipe\\x\">Remember JADE-44.</cross-session-message>"),
            User("This session is being continued. Summary: it fixed the uploader.", ""","isCompactSummary":true"""),
        ]);

        TranscriptDigest.Read(_path).ShouldBe("""
            Asked through Raven or another chat: Remember JADE-44.
            Earlier, as Claude Code summed it up when it compacted the chat: This session is being continued. Summary: it fixed the uploader.
            """.Replace("\r\n", "\n"));
    }

    [Fact]
    public void A_long_conversation_keeps_its_first_prompt_and_its_latest_steps()
    {
        File.WriteAllLines(_path, [User("Build the dark mode."), .. Enumerable.Range(1, 50).Select(i => Assistant($"Step {i} of the work, told at some length."))]);

        var digest = TranscriptDigest.Read(_path, maxChars: 300)!;

        digest.ShouldStartWith("First asked: Build the dark mode.\n[earlier steps left out]\n");
        digest.ShouldEndWith("Claude: Step 50 of the work, told at some length.");
        digest.ShouldNotContain("Step 1 of");
    }

    private static string BigResult(int chars) =>
        $$$"""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":"{{{new string('x', chars)}}}"}]}}""";

    [Fact]
    public void A_file_just_over_two_megabytes_is_read_whole_with_its_prompt_once()
    {
        File.WriteAllLines(_path, [User("Fix the icons."), BigResult(2_200_000), Assistant("Done.")]);

        TranscriptDigest.Read(_path).ShouldBe("User: Fix the icons.\nClaude: Done.");
    }

    [Fact]
    public void A_big_file_keeps_its_first_prompt_however_long_its_line()
    {
        File.WriteAllLines(_path, [User("Fix the icons. " + new string('y', 700_000)), BigResult(3_000_000), Assistant("Done.")]);

        var digest = TranscriptDigest.Read(_path)!;

        digest.ShouldStartWith("First asked: Fix the icons. yyy");
        digest.ShouldEndWith("[earlier steps left out]\nClaude: Done.");
    }

    [Fact]
    public void What_goes_along_with_a_prompt_and_stops_and_background_tasks_are_told_as_such()
    {
        File.WriteAllLines(_path, [
            User("<system-reminder>Be brief.</system-reminder>"),
            """{"type":"user","isSidechain":false,"message":{"role":"user","content":[{"type":"text","text":"<ide_opened_file>The user opened a.cs</ide_opened_file>"},{"type":"text","text":"Rename it."}]}}""",
            User("[Request interrupted by user for tool use]"),
            User("<task-notification><task-id>b1</task-id></task-notification>"),
        ]);

        TranscriptDigest.Read(_path).ShouldBe("User: Rename it.\nThe user stopped it.\nA background task of the chat ended.");
    }

    private static string Timed(string line, string at) => line.Insert(1, $"\"timestamp\":\"{at}\",");

    [Fact]
    public void Only_the_steps_of_a_stretch_of_time_are_read()
    {
        File.WriteAllLines(_path, [
            Timed(User("Old task."), "2026-10-07T10:00:00.000Z"),
            Timed(User("Fix the upload."), "2026-10-08T14:00:00.000Z"),
            Timed(Assistant("Fixed it."), "2026-10-08T14:20:00.000Z"),
            Timed(User("Next day."), "2026-10-09T09:00:00.000Z"),
            Assistant("No time on this line."),
        ]);

        TranscriptDigest.ReadBetween(_path, new DateTimeOffset(2026, 10, 8, 13, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 8, 18, 0, 0, TimeSpan.Zero))
            .ShouldBe("User: Fix the upload.\nClaude: Fixed it.");
        TranscriptDigest.ReadBetween(_path, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero))
            .ShouldBeNull();
    }

    [Fact]
    public void No_file_and_no_step_are_null()
    {
        TranscriptDigest.Read(_path).ShouldBeNull();
        TranscriptDigest.Read(null).ShouldBeNull();
        File.WriteAllLines(_path, [ToolResult, """{"type":"custom-title","customTitle":"x"}"""]);
        TranscriptDigest.Read(_path).ShouldBeNull();
    }
}
