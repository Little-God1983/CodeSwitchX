using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Ingest.Transcripts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.Ingest.Tests.Transcripts;

public class TranscriptIndexerTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "csx-idx-" + Guid.NewGuid().ToString("N"));
    private readonly ClaudeCodePaths _claude;
    private readonly string _projectDir;

    // Five minutes after the fixture timestamps, so only lines stamped "now minus a few seconds" count as recent.
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 23, 10, 5, 0, TimeSpan.Zero));
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly IUsageStore _cursors = Substitute.For<IUsageStore>();
    private readonly List<TranscriptUpdate> _updates = [];
    private readonly List<TranscriptsForgotten> _forgotten = [];
    private readonly TranscriptIndexer _indexer;

    public TranscriptIndexerTests()
    {
        _claude = new ClaudeCodePaths(_home);
        _projectDir = Path.Combine(_claude.ProjectsDirectory, "C--Repo-App");
        Directory.CreateDirectory(_projectDir);
        _cursors.GetCursorsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TranscriptCursor>>([]));
        _cursors.GetSeenMessageIdsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>([]));
        _bus.Subscribe<TranscriptUpdated>(m => _updates.Add(m.Update));
        _bus.Subscribe<TranscriptsForgotten>(_forgotten.Add);
        _indexer = new TranscriptIndexer(_claude, _cursors, _bus, _time, NullLogger<TranscriptIndexer>.Instance, new TranscriptIndexerOptions());
    }

    public void Dispose()
    {
        _indexer.Dispose();
        Directory.Delete(_home, recursive: true);
    }

    private string Transcript(string sessionId) => Path.Combine(_projectDir, sessionId + ".jsonl");

    private static string User(string session, string text, string ts = "2026-09-23T10:00:00.000Z") =>
        $$$"""{"type":"user","sessionId":"{{{session}}}","cwd":"C:\\Repo\\App","timestamp":"{{{ts}}}","message":{"role":"user","content":"{{{text}}}"}}""";

    private static string Assistant(string session, string messageId, string content, string ts = "2026-09-23T10:00:05.000Z",
        int input = 100, int output = 20, int cacheWrite = 500, int cacheRead = 3000) =>
        $$$"""{"type":"assistant","sessionId":"{{{session}}}","timestamp":"{{{ts}}}","message":{"id":"{{{messageId}}}","model":"claude-sonnet-5","role":"assistant","content":[{{{content}}}],"usage":{"input_tokens":{{{input}}},"output_tokens":{{{output}}},"cache_creation_input_tokens":{{{cacheWrite}}},"cache_read_input_tokens":{{{cacheRead}}} } } }""";

    private const string TextBlock = """{"type":"text","text":"Sure"}""";
    private const string ToolBlock = """{"type":"tool_use","id":"tu1","name":"Bash","input":{"command":"ls"}}""";

    private static string ToolResult(string session, string ts = "2026-09-23T10:00:06.000Z") =>
        $$$"""{"type":"user","sessionId":"{{{session}}}","timestamp":"{{{ts}}}","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tu1","content":"ok"}]}}""";

    private static string Summary(string title) => $$"""{"type":"summary","summary":"{{title}}","leafUuid":"x"}""";

    [Fact]
    public async Task First_scan_reports_title_cwd_model_usage_and_context()
    {
        File.WriteAllLines(Transcript("s1"),
        [
            User("s1", "Fix the build please"),
            Assistant("s1", "msg_1", TextBlock),
            Assistant("s1", "msg_1", ToolBlock),
            ToolResult("s1"),
            Assistant("s1", "msg_2", TextBlock, ts: "2026-09-23T10:00:08.000Z", input: 50, output: 5, cacheWrite: 0, cacheRead: 3600),
        ]);

        await _indexer.ScanAsync(CancellationToken.None);

        var update = _updates.ShouldHaveSingleItem();
        update.SessionId.ShouldBe("s1");
        update.TranscriptPath.ShouldBe(Transcript("s1"));
        update.Title.ShouldBe("Fix the build please");
        update.Cwd.ShouldBe(@"C:\Repo\App");
        update.Model.ShouldBe("claude-sonnet-5");
        update.Usage.Count.ShouldBe(2, "msg_1 appears twice but counts once");
        update.Usage[0].Tokens.ShouldBe(new TokenUsage(100, 20, 500, 3000));
        update.Usage[1].Tokens.ShouldBe(new TokenUsage(50, 5, 0, 3600));
        update.LatestContext.ShouldBe(new TokenUsage(50, 5, 0, 3600));
        update.LastActivityAt.ShouldBe(new DateTimeOffset(2026, 9, 23, 10, 0, 8, TimeSpan.Zero));
    }

    [Fact]
    public async Task Summary_title_beats_the_first_prompt()
    {
        File.WriteAllLines(Transcript("s1"), [Summary("Build fixes"), User("s1", "Fix the build please")]);

        await _indexer.ScanAsync(CancellationToken.None);

        _updates.ShouldHaveSingleItem().Title.ShouldBe("Build fixes");
    }

    [Fact]
    public async Task Session_id_falls_back_to_the_file_name()
    {
        File.WriteAllLines(Transcript("from-name"), ["""{"type":"user","message":{"role":"user","content":"hi"}}"""]);

        await _indexer.ScanAsync(CancellationToken.None);

        _updates.ShouldHaveSingleItem().SessionId.ShouldBe("from-name");
    }

    [Fact]
    public async Task Second_scan_only_reports_new_lines_and_dedupes_across_scans()
    {
        File.WriteAllLines(Transcript("s1"), [User("s1", "Fix"), Assistant("s1", "msg_1", TextBlock)]);
        await _indexer.ScanAsync(CancellationToken.None);
        _updates.Clear();

        await _indexer.ScanAsync(CancellationToken.None);
        _updates.ShouldBeEmpty();

        File.AppendAllLines(Transcript("s1"), [Assistant("s1", "msg_1", ToolBlock)]);
        await _indexer.ScanAsync(CancellationToken.None);

        var update = _updates.ShouldHaveSingleItem();
        update.Usage.ShouldBeEmpty();
        update.Title.ShouldBeNull("the title was already reported");
    }

    [Fact]
    public async Task A_partial_trailing_line_is_not_consumed_until_completed()
    {
        var path = Transcript("s1");
        File.WriteAllText(path, User("s1", "Fix") + "\n" + Assistant("s1", "msg_1", TextBlock)[..40]);
        await _indexer.ScanAsync(CancellationToken.None);
        _updates.ShouldHaveSingleItem().Usage.ShouldBeEmpty();
        _updates.Clear();

        File.AppendAllText(path, Assistant("s1", "msg_1", TextBlock)[40..] + "\n");
        await _indexer.ScanAsync(CancellationToken.None);

        _updates.ShouldHaveSingleItem().Usage.Count.ShouldBe(1);
    }

    private static string AiTitle(string session, string title) => $$$"""{"type":"ai-title","aiTitle":"{{{title}}}","sessionId":"{{{session}}}"}""";

    [Fact]
    public async Task A_rewritten_file_is_read_again_from_zero_without_sending_its_title_again()
    {
        var path = Transcript("s1");
        File.WriteAllLines(path, [User("s1", "go"), AiTitle("s1", "Generated title"), User("s1", "two"), User("s1", "three")]);
        await _indexer.ScanAsync(CancellationToken.None);
        _updates.ShouldHaveSingleItem().Title.ShouldBe("Generated title");
        _updates.Clear();

        // Shorter than the stored offset, so it is read again from the start. A re-read split by the byte cap would carry
        // the first prompt alone and rename the chat after it.
        File.WriteAllLines(path, [User("s1", "go")]);

        await Should.NotThrowAsync(() => _indexer.ScanAsync(CancellationToken.None));

        _updates.ShouldHaveSingleItem().Title.ShouldBeNull("the chat keeps the title it was given");
    }

    [Fact]
    public async Task Cursors_travel_with_the_update_so_they_commit_together_with_the_usage_and_restored_cursors_skip_covered_bytes()
    {
        File.WriteAllLines(Transcript("s1"), [User("s1", "Fix")]);
        await _indexer.ScanAsync(CancellationToken.None);

        var cursor = _updates.ShouldHaveSingleItem().Cursor.ShouldNotBeNull();
        cursor.SessionId.ShouldBe("s1");
        cursor.ByteOffset.ShouldBe(new FileInfo(Transcript("s1")).Length);
        await _cursors.DidNotReceive().UpsertCursorsAsync(Arg.Any<IReadOnlyCollection<TranscriptCursor>>(), Arg.Any<CancellationToken>());

        var restored = Substitute.For<IUsageStore>();
        restored.GetCursorsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TranscriptCursor>>(
        [
            new TranscriptCursor { Path = Transcript("s1").ToLowerInvariant(), ByteOffset = new FileInfo(Transcript("s1")).Length, LastWriteUtc = DateTimeOffset.UtcNow, SessionId = "s1" },
        ]));
        using var second = new TranscriptIndexer(_claude, restored, _bus, _time, NullLogger<TranscriptIndexer>.Instance, new TranscriptIndexerOptions());
        _updates.Clear();

        await second.ScanAsync(CancellationToken.None);

        _updates.ShouldBeEmpty("the restored cursor already covers the file");
    }

    [Fact]
    public async Task Inferred_signal_follows_the_spec_recent_write_is_working_then_pending_tool_is_waiting_otherwise_idle()
    {
        var recent = _time.GetUtcNow().AddSeconds(-2).ToString("O");
        File.WriteAllLines(Transcript("recent"), [User("recent", "go", recent), Assistant("recent", "m", TextBlock, ts: recent)]);
        File.WriteAllLines(Transcript("recent-pending"), [Assistant("recent-pending", "m", ToolBlock, ts: recent)]);
        File.WriteAllLines(Transcript("quiet-pending"), [Assistant("quiet-pending", "m", ToolBlock)]);
        File.WriteAllLines(Transcript("quiet"), [User("quiet", "go"), Assistant("quiet", "m", TextBlock)]);

        await _indexer.ScanAsync(CancellationToken.None);

        var recentUpdate = _updates.Single(u => u.SessionId == "recent");
        recentUpdate.InferredSignal.ShouldBe(SessionSignal.ToolUse);
        recentUpdate.PendingToolUse.ShouldBe(false);
        var recentPending = _updates.Single(u => u.SessionId == "recent-pending");
        recentPending.InferredSignal.ShouldBe(SessionSignal.ToolUse, "a fresh write is Working even with a tool call in flight");
        recentPending.PendingToolUse.ShouldBe(true);
        _updates.Single(u => u.SessionId == "quiet-pending").InferredSignal.ShouldBe(SessionSignal.Notification);
        _updates.Single(u => u.SessionId == "quiet").InferredSignal.ShouldBe(SessionSignal.Stop);
    }

    [Fact]
    public async Task Transcripts_untouched_for_longer_than_the_history_window_are_flagged_historical()
    {
        File.WriteAllLines(Transcript("old"), [User("old", "long ago"), Assistant("old", "m", TextBlock)]);
        File.SetLastWriteTimeUtc(Transcript("old"), _time.GetUtcNow().AddDays(-3).UtcDateTime);
        File.WriteAllLines(Transcript("new"), [User("new", "today")]);
        File.SetLastWriteTimeUtc(Transcript("new"), _time.GetUtcNow().AddMinutes(-5).UtcDateTime);

        await _indexer.ScanAsync(CancellationToken.None);

        _updates.Single(u => u.SessionId == "old").Historical.ShouldBeTrue();
        _updates.Single(u => u.SessionId == "old").Usage.Count.ShouldBe(1, "usage still feeds the 7-day telemetry window");
        _updates.Single(u => u.SessionId == "new").Historical.ShouldBeFalse();
    }

    [Fact]
    public async Task Subagent_transcripts_contribute_usage_to_the_parent_but_never_its_title_model_or_context()
    {
        File.WriteAllLines(Transcript("parent"), [User("parent", "Main task"), Assistant("parent", "m1", TextBlock)]);
        var subagentDir = Path.Combine(_projectDir, "parent", "subagents");
        Directory.CreateDirectory(subagentDir);
        File.WriteAllLines(Path.Combine(subagentDir, "agent-abc.jsonl"),
        [
            User("parent", "Explore the codebase"),
            Assistant("parent", "m2", ToolBlock, ts: _time.GetUtcNow().AddSeconds(-1).ToString("O"), input: 9, output: 9, cacheWrite: 9, cacheRead: 9),
        ]);

        await _indexer.ScanAsync(CancellationToken.None);

        var parent = _updates.Single(u => u.TranscriptPath == Transcript("parent"));
        parent.Title.ShouldBe("Main task");
        var subagent = _updates.Single(u => u.TranscriptPath.EndsWith("agent-abc.jsonl"));
        subagent.SessionId.ShouldBe("parent");
        subagent.Title.ShouldBeNull();
        subagent.Model.ShouldBeNull("a sub-agent may run a different model; the parent's context bar must keep the parent's");
        subagent.LatestContext.ShouldBeNull();
        subagent.Cursor.ShouldNotBeNull().Title.ShouldBeNull("a sub-agent's prompt is no chat title, so the cursor stores none");
        subagent.Usage.ShouldHaveSingleItem().Tokens.ShouldBe(new TokenUsage(9, 9, 9, 9));
    }

    [Fact]
    public async Task A_scan_that_fails_to_load_cursors_does_not_throw_and_the_next_scan_indexes_normally()
    {
        _cursors.GetCursorsAsync(Arg.Any<CancellationToken>()).Returns(
            _ => throw new InvalidOperationException("database is locked"),
            _ => Task.FromResult<IReadOnlyList<TranscriptCursor>>([]));
        File.WriteAllLines(Transcript("s1"), [User("s1", "Fix")]);

        await Should.NotThrowAsync(() => _indexer.ScanAsync(CancellationToken.None));
        _updates.ShouldBeEmpty();

        await _indexer.ScanAsync(CancellationToken.None);

        _updates.ShouldHaveSingleItem().SessionId.ShouldBe("s1");
    }

    private static string Interrupt(string session, string ts = "2026-09-23T10:00:07.000Z") =>
        $$$"""{"type":"user","sessionId":"{{{session}}}","timestamp":"{{{ts}}}","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]}}""";

    [Fact]
    public async Task An_interrupted_turn_is_reported_unless_the_user_already_moved_on()
    {
        File.WriteAllLines(Transcript("esc"), [User("esc", "go"), Assistant("esc", "m", ToolBlock), Interrupt("esc")]);
        File.WriteAllLines(Transcript("resumed"), [User("resumed", "go"), Interrupt("resumed"), User("resumed", "try again", "2026-09-23T10:00:09.000Z")]);

        await _indexer.ScanAsync(CancellationToken.None);

        var esc = _updates.Single(u => u.SessionId == "esc");
        esc.Interrupted.ShouldBeTrue();
        esc.PendingToolUse.ShouldBe(false, "an interrupted tool call is not waiting for anything");
        esc.Title.ShouldBe("go");
        _updates.Single(u => u.SessionId == "resumed").Interrupted.ShouldBeFalse();
    }

    [Fact]
    public async Task Assistant_messages_replayed_into_a_second_transcript_file_are_not_counted_twice()
    {
        File.WriteAllLines(Transcript("original"), [User("original", "go"), Assistant("original", "msg_shared", TextBlock)]);
        await _indexer.ScanAsync(CancellationToken.None);
        File.WriteAllLines(Transcript("resumed"), [User("resumed", "go"), Assistant("resumed", "msg_shared", TextBlock), Assistant("resumed", "msg_new", TextBlock, ts: "2026-09-23T10:00:09.000Z")]);

        await _indexer.ScanAsync(CancellationToken.None);

        _updates.SelectMany(u => u.Usage).Count().ShouldBe(2, "claude --resume copies earlier messages, with their usage, into a new file");
    }

    [Fact]
    public async Task Message_ids_remembered_by_the_store_are_not_counted_again_after_a_restart()
    {
        _cursors.GetSeenMessageIdsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>(["msg_shared"]));
        File.WriteAllLines(Transcript("resumed"), [User("resumed", "go"), Assistant("resumed", "msg_shared", TextBlock), Assistant("resumed", "msg_new", TextBlock, ts: "2026-09-23T10:00:09.000Z")]);

        await _indexer.ScanAsync(CancellationToken.None);

        var update = _updates.ShouldHaveSingleItem();
        update.Usage.ShouldHaveSingleItem().Tokens.ShouldBe(new TokenUsage(100, 20, 500, 3000));
        update.MessageIds.ShouldBe(["msg_new"], "only ids first seen in this pass travel to the store, in the transaction that saves their usage");
    }

    private static string Synthetic(string session, string ts = "2026-09-23T10:00:07.000Z") =>
        $$$"""{"type":"assistant","sessionId":"{{{session}}}","timestamp":"{{{ts}}}","message":{"id":"msg_err","model":"<synthetic>","role":"assistant","content":[{"type":"text","text":"API Error: 529 overloaded"}],"usage":{"input_tokens":0,"output_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0} } }""";

    [Fact]
    public async Task Synthetic_error_lines_do_not_replace_the_model_or_the_context()
    {
        File.WriteAllLines(Transcript("s1"), [User("s1", "go"), Assistant("s1", "msg_1", TextBlock), Synthetic("s1")]);

        await _indexer.ScanAsync(CancellationToken.None);

        var update = _updates.ShouldHaveSingleItem();
        update.Model.ShouldBe("claude-sonnet-5");
        update.LatestContext.ShouldBe(new TokenUsage(100, 20, 500, 3000));
        update.Usage.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_busy_subagent_keeps_its_parent_chat_working_and_a_quiet_one_moves_nothing()
    {
        var subagentDir = Path.Combine(_projectDir, "parent", "subagents");
        Directory.CreateDirectory(subagentDir);
        var busyAt = _time.GetUtcNow().AddSeconds(-1);
        File.WriteAllLines(Path.Combine(subagentDir, "agent-busy.jsonl"), [Assistant("parent", "m1", ToolBlock, ts: busyAt.ToString("O"))]);
        File.WriteAllLines(Path.Combine(subagentDir, "agent-quiet.jsonl"), [Assistant("parent", "m2", ToolBlock)]);

        await _indexer.ScanAsync(CancellationToken.None);

        // The parent is waiting on the Task call that runs the sub-agent, so the sub-agent's writes are its activity.
        var busy = _updates.Single(u => u.TranscriptPath.EndsWith("agent-busy.jsonl"));
        busy.LastActivityAt.ShouldBe(busyAt);
        busy.InferredSignal.ShouldBe(SessionSignal.ToolUse);
        // A quiet sub-agent, even one with a tool call of its own open, says nothing about whether the parent is idle.
        _updates.Single(u => u.TranscriptPath.EndsWith("agent-quiet.jsonl")).InferredSignal.ShouldBeNull();
    }

    [Fact]
    public async Task A_title_found_while_the_chat_was_historical_is_sent_when_the_chat_comes_back()
    {
        var path = Transcript("s1");
        File.WriteAllLines(path, [User("s1", "Original task"), Assistant("s1", "m1", TextBlock)]);
        File.SetLastWriteTimeUtc(path, _time.GetUtcNow().AddDays(-3).UtcDateTime);
        await _indexer.ScanAsync(CancellationToken.None);
        _updates.ShouldHaveSingleItem().Historical.ShouldBeTrue();
        _updates.Clear();

        // The chat engine drops a historical update of a chat it does not show, title and all.
        File.AppendAllLines(path, [User("s1", "continue please", _time.GetUtcNow().ToString("O"))]);
        File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime);
        await _indexer.ScanAsync(CancellationToken.None);

        var update = _updates.ShouldHaveSingleItem();
        update.Historical.ShouldBeFalse();
        update.Title.ShouldBe("Original task");
        _updates.Clear();

        File.AppendAllLines(path, [Assistant("s1", "m2", TextBlock, ts: _time.GetUtcNow().ToString("O"))]);
        await _indexer.ScanAsync(CancellationToken.None);

        _updates.ShouldHaveSingleItem().Title.ShouldBeNull("sent once, so it never undoes a later generated title");
    }

    private static string CustomTitle(string session, string title) => $$$"""{"type":"custom-title","customTitle":"{{{title}}}","sessionId":"{{{session}}}"}""";

    [Fact]
    public async Task A_name_given_with_rename_in_Claude_Code_beats_the_generated_title_and_only_a_later_one_replaces_it()
    {
        var path = Transcript("s1");
        File.WriteAllLines(path, [User("s1", "Fix the build please"), AiTitle("s1", "Build fixes"), CustomTitle("s1", "Build work")]);
        await _indexer.ScanAsync(CancellationToken.None);
        _updates.ShouldHaveSingleItem().Title.ShouldBe("Build work");
        _updates.Clear();

        File.AppendAllLines(path, [AiTitle("s1", "Build fixes, again"), CustomTitle("s1", "Build, second try")]);
        await _indexer.ScanAsync(CancellationToken.None);
        _updates.ShouldHaveSingleItem().Title.ShouldBe("Build, second try");
        _updates.Clear();

        File.AppendAllLines(path, [AiTitle("s1", "Build fixes, third"), User("s1", "one more prompt")]);
        await _indexer.ScanAsync(CancellationToken.None);

        _updates.ShouldHaveSingleItem().Title.ShouldBeNull("only another /rename replaces a /rename name");
    }

    [Fact]
    public async Task The_cursor_carries_the_title_and_where_it_came_from()
    {
        var path = Transcript("s1");
        File.WriteAllLines(path, [User("s1", "Fix the build please")]);
        await _indexer.ScanAsync(CancellationToken.None);
        var cursor = _updates.ShouldHaveSingleItem().Cursor.ShouldNotBeNull();
        cursor.Title.ShouldBe("Fix the build please");
        cursor.TitleSource.ShouldBe(TitleSource.Prompt);
        _updates.Clear();

        File.AppendAllLines(path, [AiTitle("s1", "Build fixes")]);
        await _indexer.ScanAsync(CancellationToken.None);

        cursor = _updates.ShouldHaveSingleItem().Cursor.ShouldNotBeNull();
        cursor.Title.ShouldBe("Build fixes");
        cursor.TitleSource.ShouldBe(TitleSource.Generated);
    }

    private TranscriptIndexer RestoredWith(params TranscriptCursor[] cursors)
    {
        var store = Substitute.For<IUsageStore>();
        store.GetCursorsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TranscriptCursor>>(cursors));
        store.GetSeenMessageIdsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>([]));
        return new TranscriptIndexer(_claude, store, _bus, _time, NullLogger<TranscriptIndexer>.Instance, new TranscriptIndexerOptions());
    }

    [Fact]
    public async Task A_chat_resumed_after_a_restart_gets_the_title_stored_with_its_cursor()
    {
        var path = Transcript("s1");
        File.WriteAllLines(path, [User("s1", "Original task"), AiTitle("s1", "Original work")]);
        using var restarted = RestoredWith(new TranscriptCursor
        {
            Path = path.ToLowerInvariant(), ByteOffset = new FileInfo(path).Length, LastWriteUtc = _time.GetUtcNow().AddDays(-3), SessionId = "s1",
            Title = "Original work", TitleSource = TitleSource.Generated,
        });

        // The chat engine dropped the title while the chat was historical, and the restart lost what the indexer had not delivered.
        File.AppendAllLines(path, [User("s1", "continue please", _time.GetUtcNow().ToString("O"))]);
        await restarted.ScanAsync(CancellationToken.None);

        _updates.ShouldHaveSingleItem().Title.ShouldBe("Original work");
    }

    [Fact]
    public async Task After_a_restart_a_generated_title_still_replaces_a_stored_prompt_title()
    {
        var path = Transcript("s1");
        File.WriteAllLines(path, [User("s1", "Original task")]);
        using var restarted = RestoredWith(new TranscriptCursor
        {
            Path = path.ToLowerInvariant(), ByteOffset = new FileInfo(path).Length, LastWriteUtc = _time.GetUtcNow(), SessionId = "s1",
            Title = "Original task", TitleSource = TitleSource.Prompt,
        });

        File.AppendAllLines(path, [User("s1", "second prompt", _time.GetUtcNow().ToString("O")), AiTitle("s1", "Original work")]);
        await restarted.ScanAsync(CancellationToken.None);

        _updates.ShouldHaveSingleItem().Title.ShouldBe("Original work");
    }

    [Fact]
    public async Task Transcripts_that_are_gone_are_forgotten_through_the_writer_once()
    {
        var gone = Path.Combine(_projectDir, "gone.jsonl").ToLowerInvariant();
        _cursors.GetCursorsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TranscriptCursor>>(
            [new TranscriptCursor { Path = gone, ByteOffset = 10, LastWriteUtc = _time.GetUtcNow().AddDays(-40), SessionId = "gone" }]));
        var live = Transcript("s1");
        File.WriteAllLines(live, [User("s1", "Fix")]);

        // Claude Code's cleanupPeriodDays deleted the first transcript while the app was down, the second while it runs.
        await _indexer.ScanAsync(CancellationToken.None);
        _forgotten.ShouldHaveSingleItem().Paths.ShouldBe([gone]);

        File.Delete(live);
        await _indexer.ScanAsync(CancellationToken.None);
        _forgotten.Count.ShouldBe(2);
        _forgotten[1].Paths.ShouldBe([live.ToLowerInvariant()]);

        await _indexer.ScanAsync(CancellationToken.None);
        _forgotten.Count.ShouldBe(2, "a transcript is forgotten once");
        // The writer removes the rows, in order with the cursors it still holds for the same files.
        await _cursors.DidNotReceive().RemoveCursorsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_folder_the_scan_cannot_look_into_keeps_the_cursors_of_its_transcripts()
    {
        var locked = Path.Combine(_projectDir, "locked");
        Directory.CreateDirectory(locked);
        var path = Path.Combine(locked, "s1.jsonl");
        File.WriteAllLines(path, [User("s1", "Fix")]);
        _cursors.GetCursorsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TranscriptCursor>>(
            [new TranscriptCursor { Path = path.ToLowerInvariant(), ByteOffset = new FileInfo(path).Length, LastWriteUtc = _time.GetUtcNow(), SessionId = "s1" }]));

        // A permission reset, a quarantine or a move can deny the user a folder for a while; the enumeration skips it.
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory | FileSystemRights.ReadAttributes,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny);
        var folder = new DirectoryInfo(locked);
        var acl = folder.GetAccessControl();
        acl.AddAccessRule(deny);
        folder.SetAccessControl(acl);
        try
        {
            await _indexer.ScanAsync(CancellationToken.None);
        }
        finally
        {
            acl = folder.GetAccessControl();
            acl.RemoveAccessRule(deny);
            folder.SetAccessControl(acl);
        }

        _forgotten.ShouldBeEmpty();
        await _indexer.ScanAsync(CancellationToken.None);
        _updates.ShouldBeEmpty("the cursor still covers the file once the folder opens again; nothing is read or counted twice");
    }

    [Fact]
    public async Task A_transcript_seen_in_place_outside_the_scan_is_looked_for_again_once_a_minute_not_on_every_scan()
    {
        // The Claude directory changed (CLAUDE_CONFIG_DIR): the stored cursors of the old one point at files that still exist.
        var elsewhere = Path.Combine(_home, "old-projects", "C--Repo-App");
        Directory.CreateDirectory(elsewhere);
        var path = Path.Combine(elsewhere, "s1.jsonl");
        File.WriteAllLines(path, [User("s1", "Fix")]);
        _cursors.GetCursorsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TranscriptCursor>>(
            [new TranscriptCursor { Path = path.ToLowerInvariant(), ByteOffset = new FileInfo(path).Length, LastWriteUtc = _time.GetUtcNow(), SessionId = "s1" }]));

        await _indexer.ScanAsync(CancellationToken.None);
        _forgotten.ShouldBeEmpty();

        File.Delete(path);
        await _indexer.ScanAsync(CancellationToken.None);
        _forgotten.ShouldBeEmpty("a file seen in place is not looked for on every scan, which runs every 2 s while a chat writes");

        _time.Advance(new TranscriptIndexerOptions().WatcherRefreshInterval);
        await _indexer.ScanAsync(CancellationToken.None);

        _forgotten.ShouldHaveSingleItem().Paths.ShouldBe([path.ToLowerInvariant()]);
    }

    [Fact]
    public async Task A_chat_resumed_through_a_handle_still_open_is_read_at_the_minute_refresh_although_the_folder_entry_lags()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Transcript("s1");
        File.WriteAllLines(path, [User("s1", "Original task")]);
        File.SetLastWriteTimeUtc(path, _time.GetUtcNow().AddDays(-3).UtcDateTime);
        var seen = new ConcurrentQueue<TranscriptUpdate>();
        using var subscription = _bus.Subscribe<TranscriptUpdated>(m => seen.Enqueue(m.Update));
        await _indexer.StartAsync(ct);
        try
        {
            await AdvanceAsync(TimeSpan.FromSeconds(5), ct, until: () => !seen.IsEmpty);
            seen.ShouldNotBeEmpty("the loop and its watcher are running");
            seen.Clear();

            // Claude Code appends through a handle it keeps open. Windows reports the write, to the folder entry the
            // enumeration reads and to the watcher alike, only once it reaches the disk: the scan sees the old size and the
            // old write time, and no event brings a scan. The minute refresh asks every file itself.
            var at = _time.GetUtcNow();
            await using var appending = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            appending.Write(Encoding.UTF8.GetBytes(User("s1", "continue please", at.ToString("O")) + "\n"));
            appending.Flush();
            _time.Advance(new TranscriptIndexerOptions().WatcherRefreshInterval);
            await AdvanceAsync(TimeSpan.FromSeconds(5), ct, until: () => !seen.IsEmpty);

            seen.ShouldContain(u => u.LastActivityAt == at);
        }
        finally
        {
            await _indexer.StopAsync(ct);
        }
    }

    [Fact]
    public async Task A_rewrite_does_not_offer_the_earlier_rename_names_again()
    {
        var path = Transcript("s1");
        string[] lines = [User("s1", "go"), CustomTitle("s1", "First name"), User("s1", "two"), CustomTitle("s1", "Second name"), User("s1", "three")];
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        await _indexer.ScanAsync(CancellationToken.None);
        _updates.ShouldHaveSingleItem().Title.ShouldBe("Second name");
        _updates.Clear();

        // Claude Code cut the last prompt out; the file is read again from the start.
        File.WriteAllText(path, string.Join('\n', lines[..4]) + "\n");
        await _indexer.ScanAsync(CancellationToken.None);

        _updates.ShouldHaveSingleItem().Title.ShouldBeNull("the chat keeps the name it has");
    }

    [Fact]
    public async Task A_re_read_cut_by_the_byte_cap_between_two_rename_names_does_not_show_the_earlier_one()
    {
        var path = Transcript("s1");
        string[] lines = [User("s1", "go"), CustomTitle("s1", "First name"), User("s1", "two"), CustomTitle("s1", "Second name"), User("s1", "three")];
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        var options = new TranscriptIndexerOptions { MaxBytesPerPass = Encoding.UTF8.GetByteCount(string.Join('\n', lines[..3]) + "\n") + 1 };
        using var capped = new TranscriptIndexer(_claude, _cursors, _bus, _time, NullLogger<TranscriptIndexer>.Instance, options);
        await capped.ScanAsync(CancellationToken.None);
        await capped.ScanAsync(CancellationToken.None);
        _updates.Select(u => u.Title).ShouldBe(["First name", "Second name"]);
        _updates.Clear();

        File.WriteAllText(path, string.Join('\n', lines[..4]) + "\n");
        await capped.ScanAsync(CancellationToken.None);
        await capped.ScanAsync(CancellationToken.None);

        _updates.Select(u => u.Title).ShouldBe([null, null], "neither pass of the re-read renames the chat");
    }

    [Fact]
    public async Task A_chat_renamed_before_the_upgrade_gets_its_name_from_behind_the_stored_offset_without_counting_its_usage_again()
    {
        var path = Transcript("s1");
        File.WriteAllLines(path, [User("s1", "go"), Assistant("s1", "m1", TextBlock), CustomTitle("s1", "Old name")]);
        // The migration marks every cursor stored before as having found a prompt title, with no title to show.
        using var upgraded = RestoredWith(new TranscriptCursor
        {
            Path = path.ToLowerInvariant(), ByteOffset = new FileInfo(path).Length, LastWriteUtc = new DateTimeOffset(2026, 9, 23, 10, 0, 10, TimeSpan.Zero),
            SessionId = "s1", TitleSource = TitleSource.Prompt,
        });

        await upgraded.ScanAsync(CancellationToken.None);

        var update = _updates.ShouldHaveSingleItem();
        update.Title.ShouldBe("Old name");
        update.Usage.ShouldBeEmpty("counted before the upgrade");
        update.Cursor.ShouldNotBeNull().ByteOffset.ShouldBe(new FileInfo(path).Length);
    }

    private static string LastPrompt(string session) => $$$"""{"type":"last-prompt","lastPrompt":"go","sessionId":"{{{session}}}"}""";

    [Fact]
    public async Task A_pass_of_lines_without_a_timestamp_leaves_the_state_alone()
    {
        var recent = _time.GetUtcNow().AddSeconds(-1).ToString("O");
        File.WriteAllLines(Transcript("s1"), [User("s1", "go", recent), Assistant("s1", "m1", ToolBlock, ts: recent)]);
        await _indexer.ScanAsync(CancellationToken.None);
        _updates.Clear();

        // Claude Code writes its metadata lines (last-prompt, ai-title, mode, ...) without a timestamp, some while it works.
        File.AppendAllLines(Transcript("s1"), [LastPrompt("s1")]);
        await _indexer.ScanAsync(CancellationToken.None);

        _updates.ShouldHaveSingleItem().InferredSignal.ShouldBeNull("the line says nothing about whether Claude is working");
    }

    [Fact]
    public async Task A_catch_up_split_by_the_byte_cap_moves_the_state_only_with_its_last_part()
    {
        // The first part ends with a tool call whose result is in the second.
        string[] lines = [User("s1", "go"), Assistant("s1", "m1", ToolBlock), ToolResult("s1")];
        File.WriteAllText(Transcript("s1"), string.Join('\n', lines) + "\n");
        var options = new TranscriptIndexerOptions { MaxBytesPerPass = Encoding.UTF8.GetByteCount(lines[0] + "\n" + lines[1] + "\n") + 1 };
        using var capped = new TranscriptIndexer(_claude, _cursors, _bus, _time, NullLogger<TranscriptIndexer>.Instance, options);

        await capped.ScanAsync(CancellationToken.None);

        var first = _updates.ShouldHaveSingleItem();
        first.Usage.ShouldHaveSingleItem();
        first.InferredSignal.ShouldBeNull("the tool call is only open in the middle of the file");
        first.PendingToolUse.ShouldBeNull();
        _updates.Clear();

        await capped.ScanAsync(CancellationToken.None);

        var last = _updates.ShouldHaveSingleItem();
        last.InferredSignal.ShouldBe(SessionSignal.Stop);
        last.PendingToolUse.ShouldBe(false);
    }

    [Fact]
    public async Task An_interrupt_in_the_first_part_of_a_split_catch_up_is_not_reported()
    {
        string[] lines = [User("s1", "go"), Interrupt("s1"), User("s1", "try again", "2026-09-23T10:00:09.000Z")];
        File.WriteAllText(Transcript("s1"), string.Join('\n', lines) + "\n");
        var options = new TranscriptIndexerOptions { MaxBytesPerPass = Encoding.UTF8.GetByteCount(lines[0] + "\n" + lines[1] + "\n") + 1 };
        using var capped = new TranscriptIndexer(_claude, _cursors, _bus, _time, NullLogger<TranscriptIndexer>.Instance, options);

        await capped.ScanAsync(CancellationToken.None);

        _updates.ShouldHaveSingleItem().Interrupted.ShouldBeFalse("the chat went on after it");
    }

    [Fact]
    public async Task A_rewrite_that_removes_an_indexed_line_does_not_count_the_earlier_usage_again()
    {
        // Two remembered message ids stand in for a long history, after whose first index a chat's early ids are forgotten.
        using var indexer = new TranscriptIndexer(_claude, _cursors, _bus, _time, NullLogger<TranscriptIndexer>.Instance,
            new TranscriptIndexerOptions { MessageIdMemory = 2 });
        var path = Transcript("s1");
        string[] lines =
        [
            Assistant("s1", "m1", TextBlock, ts: "2026-09-23T10:00:01.000Z"),
            Assistant("s1", "m2", TextBlock, ts: "2026-09-23T10:00:02.000Z"),
            Assistant("s1", "m3", TextBlock, ts: "2026-09-23T10:00:03.000Z"),
        ];
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 9, 23, 10, 0, 4, DateTimeKind.Utc));
        await indexer.ScanAsync(CancellationToken.None);

        // Claude Code retracts a streamed message by cutting the file at its line and writing back what followed.
        File.WriteAllText(path, lines[0] + "\n" + lines[1] + "\n");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 9, 23, 10, 0, 6, DateTimeKind.Utc));
        await indexer.ScanAsync(CancellationToken.None);

        _updates.SelectMany(u => u.Usage).Count().ShouldBe(3);
    }

    [Fact]
    public async Task A_rewrite_followed_by_new_lines_is_read_again_from_the_start()
    {
        var path = Transcript("s1");
        string[] lines = [User("s1", "go"), Assistant("s1", "m1", TextBlock, ts: "2026-09-23T10:00:01.000Z"), Assistant("s1", "m2", TextBlock, ts: "2026-09-23T10:00:02.000Z")];
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 9, 23, 10, 0, 3, DateTimeKind.Utc));
        await _indexer.ScanAsync(CancellationToken.None);
        _updates.Clear();

        // m2 is retracted and its longer retry is written before the next scan, so the file is longer than the offset.
        var retry = Assistant("s1", "m2-retry", ToolBlock, ts: "2026-09-23T10:00:08.000Z");
        File.WriteAllText(path, lines[0] + "\n" + lines[1] + "\n" + retry + "\n");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 9, 23, 10, 0, 9, DateTimeKind.Utc));
        await _indexer.ScanAsync(CancellationToken.None);

        _updates.SelectMany(u => u.Usage).ShouldHaveSingleItem().At.ShouldBe(new DateTimeOffset(2026, 9, 23, 10, 0, 8, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_rewrite_counts_usage_stamped_before_the_last_indexed_write_that_was_not_read_yet()
    {
        // Claude Code stamps an assistant line when its message starts, which can be seconds before the line above it
        // was written.
        var path = Transcript("s1");
        File.WriteAllText(path, User("s1", "go") + "\n" + Assistant("s1", "m1", TextBlock, ts: "2026-09-23T10:00:01.000Z") + "\n" + LastPrompt("s1") + "\n");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 9, 23, 10, 0, 10, DateTimeKind.Utc));
        await _indexer.ScanAsync(CancellationToken.None);
        _updates.Clear();

        // m1 is retracted, and the retry, stamped before the last indexed write, is written.
        var retry = Assistant("s1", "m2", ToolBlock, ts: "2026-09-23T10:00:05.000Z");
        File.WriteAllText(path, User("s1", "go") + "\n" + LastPrompt("s1") + "\n" + retry + "\n");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 9, 23, 10, 0, 12, DateTimeKind.Utc));
        await _indexer.ScanAsync(CancellationToken.None);

        _updates.SelectMany(u => u.Usage).ShouldHaveSingleItem().At.ShouldBe(new DateTimeOffset(2026, 9, 23, 10, 0, 5, TimeSpan.Zero));
    }

    [Fact]
    public async Task After_a_restart_a_rewrite_does_not_count_the_usage_before_the_stored_cursor_again()
    {
        var path = Transcript("s1");
        string[] lines =
        [
            Assistant("s1", "m1", TextBlock, ts: "2026-09-23T10:00:01.000Z"),
            Assistant("s1", "m2", TextBlock, ts: "2026-09-23T10:00:02.000Z"),
            Assistant("s1", "m3", TextBlock, ts: "2026-09-23T10:00:03.000Z"),
        ];
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        // The cursor covers the whole file, and the store no longer remembers its message ids.
        _cursors.GetCursorsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TranscriptCursor>>(
        [
            new TranscriptCursor { Path = path.ToLowerInvariant(), ByteOffset = new FileInfo(path).Length, LastWriteUtc = new DateTimeOffset(2026, 9, 23, 10, 0, 4, TimeSpan.Zero), SessionId = "s1" },
        ]));

        File.WriteAllText(path, lines[0] + "\n" + lines[1] + "\n");
        await _indexer.ScanAsync(CancellationToken.None);

        _updates.SelectMany(u => u.Usage).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_interrupt_that_ends_the_first_part_of_a_split_catch_up_is_reported_with_the_last()
    {
        string[] lines = [User("s1", "go"), Assistant("s1", "m1", ToolBlock), Interrupt("s1"), LastPrompt("s1")];
        File.WriteAllText(Transcript("s1"), string.Join('\n', lines) + "\n");
        var options = new TranscriptIndexerOptions { MaxBytesPerPass = Encoding.UTF8.GetByteCount(string.Join('\n', lines[..3]) + "\n") + 1 };
        using var capped = new TranscriptIndexer(_claude, _cursors, _bus, _time, NullLogger<TranscriptIndexer>.Instance, options);

        await capped.ScanAsync(CancellationToken.None);
        await capped.ScanAsync(CancellationToken.None);

        _updates.Select(u => u.Interrupted).ShouldBe([false, true]);
    }

    [Fact]
    public async Task A_workflow_journal_belongs_to_the_session_that_holds_it_not_to_a_chat_of_its_own()
    {
        // Claude Code keeps a workflow's journal under the session's subagents folder; its lines carry no sessionId.
        var runDir = Path.Combine(_projectDir, "parent", "subagents", "workflows", "run1");
        Directory.CreateDirectory(runDir);
        File.WriteAllLines(Path.Combine(runDir, "journal.jsonl"), ["""{"type":"started","key":"k1","agentId":"a1"}"""]);

        await _indexer.ScanAsync(CancellationToken.None);

        _updates.ShouldHaveSingleItem().SessionId.ShouldBe("parent");
    }

    [Fact]
    public async Task Indexing_goes_on_after_the_transcript_folder_is_deleted_and_created_again()
    {
        var ct = TestContext.Current.CancellationToken;
        // No fresh watcher within the test, so only the watcher's error can bring the scans back.
        using var indexer = new TranscriptIndexer(_claude, _cursors, _bus, _time, NullLogger<TranscriptIndexer>.Instance,
            new TranscriptIndexerOptions { WatcherRefreshInterval = TimeSpan.FromDays(1) });
        var seen = new ConcurrentQueue<TranscriptUpdate>();
        using var subscription = _bus.Subscribe<TranscriptUpdated>(m => seen.Enqueue(m.Update));
        File.WriteAllLines(Transcript("before"), [User("before", "Fix")]);
        await indexer.StartAsync(ct);
        try
        {
            await AdvanceAsync(TimeSpan.FromSeconds(5), ct, until: () => seen.Any(u => u.SessionId == "before"));
            seen.ShouldContain(u => u.SessionId == "before", "the loop and its folder watcher are running");

            // Deleting the folder ends the watcher for good. Its error still triggers one scan, which finds no folder.
            Directory.Delete(_claude.ProjectsDirectory, recursive: true);
            await AdvanceAsync(TimeSpan.FromSeconds(1), ct);
            Directory.CreateDirectory(_projectDir);
            File.WriteAllLines(Transcript("after"), [User("after", "Fix")]);
            await AdvanceAsync(TimeSpan.FromSeconds(5), ct, until: () => seen.Any(u => u.SessionId == "after"));

            seen.ShouldContain(u => u.SessionId == "after");
        }
        finally
        {
            await indexer.StopAsync(ct);
        }
    }

    [Fact]
    public async Task Indexing_goes_on_after_the_transcript_folder_is_renamed_away_and_a_new_one_created()
    {
        var ct = TestContext.Current.CancellationToken;
        var seen = new ConcurrentQueue<TranscriptUpdate>();
        using var subscription = _bus.Subscribe<TranscriptUpdated>(m => seen.Enqueue(m.Update));
        File.WriteAllLines(Transcript("before"), [User("before", "Fix")]);
        await _indexer.StartAsync(ct);
        try
        {
            await AdvanceAsync(TimeSpan.FromSeconds(5), ct, until: () => seen.Any(u => u.SessionId == "before"));
            seen.ShouldContain(u => u.SessionId == "before", "the loop and its folder watcher are running");

            // The watcher moves with the renamed folder and reports no error, so it never sees the new one.
            Directory.Move(_claude.ProjectsDirectory, _claude.ProjectsDirectory + "-old");
            Directory.CreateDirectory(_projectDir);
            File.WriteAllLines(Transcript("after"), [User("after", "Fix")]);
            await AdvanceAsync(TimeSpan.FromSeconds(5), ct, until: () => seen.Any(u => u.SessionId == "after"));

            seen.ShouldContain(u => u.SessionId == "after");
        }
        finally
        {
            await _indexer.StopAsync(ct);
        }
    }

    private static readonly TimeSpan Interval = new TranscriptIndexerOptions().ScanInterval;

    /// <summary>Holds a transcript open without sharing, with one more line in it: a pass cannot open it, and no further write is due to come.</summary>
    private FileStream Lock(string sessionId, string line)
    {
        var locked = new FileStream(Transcript(sessionId), FileMode.Append, FileAccess.Write, FileShare.None);
        locked.Write(Encoding.UTF8.GetBytes(line + "\n"));
        locked.Flush();
        return locked;
    }

    [Fact]
    public async Task A_transcript_that_cannot_be_read_in_one_pass_is_read_again_by_itself_not_by_a_scan_of_everything()
    {
        File.WriteAllLines(Transcript("s1"), [User("s1", "Fix")]);
        File.WriteAllLines(Transcript("s2"), [User("s2", "Test")]);
        await _indexer.TickAsync(CancellationToken.None);
        _updates.Count.ShouldBe(2);

        using (Lock("s1", Assistant("s1", "msg_1", TextBlock)))
        {
            await _indexer.TickAsync(CancellationToken.None);
        }

        _updates.Count.ShouldBe(2, "the locked file cannot be read in that pass");
        _indexer.Dirty.ShouldBeFalse("one unreadable file must not make every tick a scan of everything");

        // With the watcher running, a change it did not report is not seen: the next tick reads only the file the pass could not.
        File.AppendAllLines(Transcript("s2"), [Assistant("s2", "msg_2", TextBlock)]);
        _indexer.StartWatcher();
        _time.Advance(Interval);
        await _indexer.TickAsync(CancellationToken.None);

        _updates.Count.ShouldBe(3);
        _updates[2].SessionId.ShouldBe("s1");
        _updates[2].Usage.ShouldHaveSingleItem();
        _indexer.NextAttempt(Transcript("s1")).ShouldBeNull("once read, the file is no longer due for another try");
    }

    [Fact]
    public async Task A_transcript_that_stays_unreadable_is_tried_less_and_less_often_up_to_the_cap()
    {
        using var indexer = new TranscriptIndexer(_claude, _cursors, _bus, _time, NullLogger<TranscriptIndexer>.Instance,
            new TranscriptIndexerOptions { MaxRetryDelay = TimeSpan.FromSeconds(5) });
        File.WriteAllLines(Transcript("s1"), [User("s1", "Fix")]);
        await indexer.TickAsync(CancellationToken.None);
        using var locked = Lock("s1", Assistant("s1", "msg_1", TextBlock));
        await indexer.TickAsync(CancellationToken.None);
        indexer.StartWatcher();
        var failedAt = _time.GetUtcNow();

        indexer.NextAttempt(Transcript("s1")).ShouldBe(failedAt + Interval, "the first try is the next tick: a sharing violation on a chat's last write clears at once");

        _time.Advance(Interval);
        await indexer.TickAsync(CancellationToken.None);
        indexer.NextAttempt(Transcript("s1")).ShouldBe(_time.GetUtcNow() + 2 * Interval, "the second try fails: twice as long");

        _time.Advance(Interval);
        await indexer.TickAsync(CancellationToken.None);
        indexer.NextAttempt(Transcript("s1")).ShouldBe(failedAt + 3 * Interval, "not due yet: not tried");

        _time.Advance(Interval);
        await indexer.TickAsync(CancellationToken.None);
        indexer.NextAttempt(Transcript("s1")).ShouldBe(_time.GetUtcNow() + TimeSpan.FromSeconds(5), "the third try fails: capped, not four intervals");
        _updates.ShouldHaveSingleItem("nothing was read");
        indexer.Dirty.ShouldBeFalse();
    }

    /// <summary>Moves the indexer's timer on one scan interval at a time, for up to <paramref name="realTime"/>, giving each scan time to run.</summary>
    private async Task AdvanceAsync(TimeSpan realTime, CancellationToken ct, Func<bool>? until = null)
    {
        var deadline = DateTime.UtcNow + realTime;
        while (DateTime.UtcNow < deadline && until?.Invoke() != true)
        {
            _time.Advance(new TranscriptIndexerOptions().ScanInterval);
            await Task.Delay(50, ct);
        }
    }
}
