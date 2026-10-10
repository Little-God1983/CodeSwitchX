using System.Text;
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

    // #236: the same task sent twice: the end read has the second, not the first, and the digest must say steps were left out
    [Fact]
    public void A_big_file_whose_latest_steps_repeat_the_first_prompt_still_says_steps_were_left_out()
    {
        File.WriteAllLines(_path, [User("Run the release."), BigResult(3_000_000), Assistant("Released."), User("Run the release."), Assistant("Released again.")]);

        var digest = TranscriptDigest.Read(_path)!;

        digest.ShouldStartWith("First asked: Run the release.\n[earlier steps left out]\n");
        digest.ShouldEndWith("User: Run the release.\nClaude: Released again.");
    }

    // #236: a file read whole, its first prompt not among the steps kept but a later one of the same words: still told first
    [Fact]
    public void A_later_prompt_of_the_first_prompt_s_words_does_not_stand_for_it()
    {
        File.WriteAllLines(_path, [User("Run the release."), .. Enumerable.Range(1, 20).Select(i => Assistant($"Step {i} of the release, told at some length.")),
            User("Run the release."), Assistant("Done.")]);

        var digest = TranscriptDigest.Read(_path, maxChars: 120)!;

        digest.ShouldStartWith("First asked: Run the release.\n[earlier steps left out]\n");
        digest.ShouldEndWith("User: Run the release.\nClaude: Done.");
    }

    /// <summary>A prompt with pasted images: its words in a text block, each image's data a long string of its own.</summary>
    private static string UserWithImages(string text, int images, int chars)
    {
        var image = "{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"" + new string('A', chars) + "\"}},";
        return "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":[" + string.Concat(Enumerable.Repeat(image, images))
            + "{\"type\":\"text\",\"text\":" + System.Text.Json.JsonSerializer.Serialize(text) + "}]}}";
    }

    // #236: a first prompt of several pasted images, megabytes long: its words are kept, its images' data never held
    [Fact]
    public void A_first_prompt_of_several_pasted_images_keeps_its_words()
    {
        File.WriteAllLines(_path, [UserWithImages("Make it look like \"these\".", images: 3, chars: 2_000_000), BigResult(3_000_000), Assistant("Done.")]);

        TranscriptDigest.Read(_path)!.ShouldStartWith("First asked: Make it look like \"these\".\n");
    }

    // #236: a prompt given as one string, with a reminder or VS Code's selection before its words, keeps its words
    [Theory]
    [InlineData("<system-reminder>Be brief.</system-reminder>\nRename it.", "User: Rename it.")]
    [InlineData("<ide_selection>The user selected Upload.cs:3</ide_selection> Explain this.", "User: Explain this.")]
    [InlineData("<system-reminder>a</system-reminder><ide_opened_file>b</ide_opened_file>Two before.", "User: Two before.")]
    [InlineData("<system-reminder>Never closed. Rename it.", null)]
    [InlineData("<ide_opened_file\npath=\"a.cs\">The user opened a.cs</ide_opened_file> Explain it.", "User: Explain it.")]
    [InlineData("<system-reminder/>Do it.", "User: Do it.")]
    [InlineData("<ide_opened_file /> Explain it.", "User: Explain it.")]
    [InlineData("<system-reminders> are noisy, remove them.", "User: <system-reminders> are noisy, remove them.")]
    [InlineData("<system-reminder>outer <system-reminder>inner</system-reminder> still outer</system-reminder> Do it.", "User: Do it.")] // #258
    [InlineData("<system-reminder>a <system-reminders> b</system-reminder> Do it.", "User: Do it.")]
    [InlineData("<system-reminder>Wrapped in <system-reminder> tags.</system-reminder> Rename it.", "User: Rename it.")] // round 1 of #258
    [InlineData("<system-reminder>a <system-reminder /> b</system-reminder> Do it.", "User: Do it.")]
    public void A_prompt_with_a_reminder_or_a_selection_before_its_words_keeps_its_words(string prompt, string? step)
    {
        File.WriteAllLines(_path, [User(prompt)]);

        TranscriptDigest.Read(_path).ShouldBe(step);
    }

    // #236: a long prompt is cut between its escapes ("é"), never inside one: it still parses
    [Fact]
    public void A_long_first_prompt_full_of_escapes_is_cut_where_it_still_parses()
    {
        File.WriteAllLines(_path, [User("Fix it. " + new string('é', 70_000)), BigResult(3_000_000), Assistant("Done.")]);

        TranscriptDigest.Read(_path)!.ShouldStartWith("First asked: Fix it. éé");
    }

    // Review of #236: an emoji right at the cut, escaped or raw: never cut between its two halves, which would not parse
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_long_first_prompt_is_never_cut_between_the_halves_of_an_emoji(bool escaped)
    {
        var emoji = char.ConvertFromUtf32(0x1F600);
        // Escaped, the emoji is two six-char escapes, the first ending at the cut; raw, two chars, the first the last kept.
        var line = escaped
            ? User("Fix it. " + new string('a', TranscriptDigest.LongestString - 8 - 6) + emoji + " and the rest.")
            : "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"Fix it. "
                + new string('a', TranscriptDigest.LongestString - 8 - 1) + emoji + " and the rest.\"}}";
        File.WriteAllLines(_path, [line, BigResult(3_000_000), Assistant("Done.")]);

        TranscriptDigest.Read(_path)!.ShouldStartWith("First asked: Fix it. aaa");
    }

    // #258: a reminder or selection longer than the longest string kept, before the words: the end of the string is kept too
    [Theory]
    [InlineData("r")]
    [InlineData("é")]
    public void A_big_file_s_first_prompt_after_a_long_reminder_keeps_its_words(string filler)
    {
        var reminder = "<system-reminder>" + string.Concat(Enumerable.Repeat(filler, 100_000)) + "</system-reminder>\nRename it.";
        File.WriteAllLines(_path, [User(reminder), BigResult(3_000_000), Assistant("Done.")]);

        TranscriptDigest.Read(_path)!.ShouldStartWith("First asked: Rename it.\n");
    }

    // #258: the end kept begins where it still parses: never between the halves of an emoji, escaped or raw
    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 5)]
    [InlineData(true, 6)]
    [InlineData(true, 7)]
    [InlineData(true, 11)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    public void The_end_kept_of_a_long_reminder_full_of_emoji_still_parses(bool escaped, int pad)
    {
        var emoji = char.ConvertFromUtf32(0x1F600);
        // Past the longest string kept: escaped, an emoji is 12 chars; raw, 2.
        // The pad after them moves where the end kept begins: on each char of an escaped pair, or either half of a raw one.
        var inner = string.Concat(Enumerable.Repeat(emoji, escaped ? 20_000 : 60_000)) + new string('p', pad);
        var line = escaped
            ? User("<system-reminder>" + inner + "</system-reminder> Rename it.")
            : "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"<system-reminder>" + inner
                + "</system-reminder> Rename it.\"}}";
        File.WriteAllLines(_path, [line, BigResult(3_000_000), Assistant("Done.")]);

        TranscriptDigest.Read(_path)!.ShouldStartWith("First asked: Rename it.\n");
    }

    // #258: the first prompt in the end read is found by its line's id, not by counting bytes: a byte order mark shifted the count
    [Fact]
    public void The_end_read_finds_the_first_prompt_by_its_line_s_id()
    {
        var first = BigResult(600_000);
        var prompt = User("Fix the icons.", extra: ",\"uuid\":\"u-first\"");
        var done = Assistant("Done.");
        // The end read begins two bytes before the prompt's line: in the last char of the line before and its newline. Counted
        // without the byte order mark, its line began three bytes earlier, before the end read.
        var bom = Encoding.UTF8.GetPreamble().Length;
        var tailAt = (long)first.Length + 2;
        var fillerChars = (int)(tailAt + TranscriptDigest.TailBytes - bom - (first.Length + 1) - (prompt.Length + 1) - done.Length
            - 1 - BigResult(0).Length); // the filler's newline
        File.WriteAllText(_path, string.Join("\n", first, prompt, BigResult(fillerChars), done), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        new FileInfo(_path).Length.ShouldBe(tailAt + TranscriptDigest.TailBytes);

        TranscriptDigest.Read(_path).ShouldBe("User: Fix the icons.\nClaude: Done.", "the end read has the first prompt: told once, nothing left out");
    }

    // Round 1 of #258: a string a little longer than the start kept loses no char between its start and its end
    [Fact]
    public void A_reminder_whose_closing_tag_straddles_the_start_kept_is_still_closed()
    {
        const string open = "<system-reminder>";
        // Written as it is, not escaped as User() writes "<": "</syst" is the last of the start, "em-reminder>" begins the end.
        var reminder = open + new string('r', TranscriptDigest.LongestString - 6 - open.Length) + "</system-reminder> Rename it.";
        var line = "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"" + reminder + "\"}}";
        File.WriteAllLines(_path, [line, BigResult(3_000_000), Assistant("Done.")]);

        TranscriptDigest.Read(_path)!.ShouldStartWith("First asked: Rename it.\n");
    }

    /// <summary>A big file: <paramref name="lines"/>, then a filler, then <paramref name="last"/>, its end read beginning at <paramref name="tailAt"/>.</summary>
    private void WriteWithEndReadAt(long tailAt, string last, Encoding encoding, params string[] lines)
    {
        var bom = encoding.GetPreamble().Length;
        var before = lines.Sum(l => (long)l.Length + 1);
        var fillerChars = (int)(tailAt + TranscriptDigest.TailBytes - bom - before - last.Length - 1 - BigResult(0).Length);
        File.WriteAllText(_path, string.Join("\n", [.. lines, BigResult(fillerChars), last]), encoding);
        new FileInfo(_path).Length.ShouldBe(tailAt + TranscriptDigest.TailBytes);
    }

    // Round 1 of #258: a first prompt whose line has no id is still found in the end read by where it is
    [Fact]
    public void A_first_prompt_without_an_id_is_told_once_when_the_end_read_has_it()
    {
        var first = BigResult(600_000);
        WriteWithEndReadAt(first.Length - 100, Assistant("Done."), new UTF8Encoding(false), first, User("Fix the icons."));

        TranscriptDigest.Read(_path).ShouldBe("User: Fix the icons.\nClaude: Done.");
    }

    // Round 1 of #258: a resumed chat writes its first prompt again, under the same id: that copy is not the first prompt
    [Fact]
    public void A_copy_of_the_first_prompt_s_line_written_on_a_resume_does_not_stand_for_it()
    {
        var prompt = User("Fix the icons.", extra: ",\"uuid\":\"u-first\"");
        File.WriteAllLines(_path, [prompt, BigResult(3_000_000), Assistant("Resumed."), prompt, Assistant("Done.")]);

        TranscriptDigest.Read(_path).ShouldBe("First asked: Fix the icons.\n[earlier steps left out]\nClaude: Resumed.\nUser: Fix the icons.\nClaude: Done.");
    }

    // Round 1 of #258: a user line too long to read may be the first prompt: a later one does not stand for it
    [Fact]
    public void A_user_line_too_long_to_read_in_the_head_leaves_the_first_prompt_unknown()
    {
        var blocks = string.Join(",", Enumerable.Repeat("""{"type":"text","text":"ab"}""", 45_000));
        var huge = "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":[" + blocks + "]}}";
        huge.Length.ShouldBeGreaterThan(TranscriptDigest.LongestLine);
        File.WriteAllLines(_path, [huge, User("A later one."), BigResult(3_000_000), Assistant("Done.")]);

        TranscriptDigest.Read(_path).ShouldBe("[earlier steps left out]\nClaude: Done.");
    }

    // Review of #236: a text block of a prompt with a reminder before its words keeps them, as a prompt of one string does
    [Fact]
    public void A_text_block_with_a_reminder_before_its_words_keeps_them()
    {
        File.WriteAllLines(_path, [
            """{"type":"user","isSidechain":false,"message":{"role":"user","content":[{"type":"text","text":"<system-reminder>Be brief.</system-reminder>\nRename it."}]}}""",
        ]);

        TranscriptDigest.Read(_path).ShouldBe("User: Rename it.");
    }

    // Review of #236: no prompt in the head of a big file: the end read does not stand for the first prompt
    [Fact]
    public void A_big_file_with_no_prompt_in_its_head_still_says_steps_were_left_out()
    {
        File.WriteAllLines(_path, [BigResult(TranscriptDigest.HeadChars + 1000), User("The real first task."), BigResult(3_000_000),
            User("A later one."), Assistant("Done.")]);

        TranscriptDigest.Read(_path)!.ShouldStartWith("[earlier steps left out]\nUser: A later one.");
    }

    // Review of #236: no prompt in the head, but the end read begins before the head stopped: it has the real first prompt
    [Fact]
    public void A_big_file_whose_end_read_begins_inside_its_head_has_the_first_prompt()
    {
        File.WriteAllLines(_path, [BigResult(TranscriptDigest.HeadChars + 1000), User("The real first task."), Assistant("Done.")]);

        TranscriptDigest.Read(_path).ShouldBe("User: The real first task.\nClaude: Done.");
    }

    private static string Timed(string line, string at) => line.Insert(1, $"\"timestamp\":\"{at}\",");

    [Fact]
    public void Only_the_steps_of_a_stretch_of_time_are_read()
    {
        File.WriteAllLines(_path, [
            Timed(User("Old task."), "2026-10-07T10:00:00.000Z"),
            Timed(User("Fix the upload."), "2026-10-08T14:00:00.000Z"),
            Timed(Assistant("Fixed it.").Insert(1, "\"uuid\":\"u2\","), "2026-10-08T14:20:00.000Z"),
            Timed(Assistant("Fixed it.").Insert(1, "\"uuid\":\"u2\","), "2026-10-08T14:20:00.000Z"), // a resumed chat writes it again
            Assistant("No time on this line."),
            Timed(User("Next day."), "2026-10-09T09:00:00.000Z"),
        ]);

        var ct = TestContext.Current.CancellationToken;
        TranscriptDigest.ReadBetween(_path, new DateTimeOffset(2026, 10, 8, 13, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 8, 18, 0, 0, TimeSpan.Zero), ct: ct)
            .ShouldBe("User: Fix the upload.\nClaude: Fixed it.");
        TranscriptDigest.ReadBetween(_path, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), ct: ct)
            .ShouldBeNull("long past the end, the reading stops");
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
