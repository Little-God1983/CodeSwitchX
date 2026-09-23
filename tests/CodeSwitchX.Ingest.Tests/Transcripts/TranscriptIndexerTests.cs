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
    private readonly TranscriptIndexer _indexer;

    public TranscriptIndexerTests()
    {
        _claude = new ClaudeCodePaths(_home);
        _projectDir = Path.Combine(_claude.ProjectsDirectory, "C--Repo-App");
        Directory.CreateDirectory(_projectDir);
        _cursors.GetCursorsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TranscriptCursor>>([]));
        _cursors.GetSeenMessageIdsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>([]));
        _bus.Subscribe<TranscriptUpdated>(m => _updates.Add(m.Update));
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

    [Fact]
    public async Task Truncated_file_restarts_from_zero_without_throwing()
    {
        var path = Transcript("s1");
        File.WriteAllLines(path, [User("s1", "one"), User("s1", "two"), User("s1", "three")]);
        await _indexer.ScanAsync(CancellationToken.None);
        _updates.Clear();

        File.WriteAllLines(path, [User("s1", "fresh")]);

        await Should.NotThrowAsync(() => _indexer.ScanAsync(CancellationToken.None));

        _updates.ShouldHaveSingleItem().Title.ShouldBe("fresh");
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
    public async Task Subagent_transcripts_contribute_usage_to_the_parent_but_never_its_title_state_or_context()
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
        subagent.InferredSignal.ShouldBeNull();
        subagent.LatestContext.ShouldBeNull();
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
}
