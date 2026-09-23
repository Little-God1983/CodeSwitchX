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
    public async Task Cursors_are_persisted_after_each_scan_and_restored_on_start()
    {
        File.WriteAllLines(Transcript("s1"), [User("s1", "Fix")]);
        await _indexer.ScanAsync(CancellationToken.None);

        await _cursors.Received(1).UpsertCursorsAsync(
            Arg.Is<IReadOnlyCollection<TranscriptCursor>>(c => c.Count == 1 && c.First().SessionId == "s1" && c.First().ByteOffset == new FileInfo(Transcript("s1")).Length),
            Arg.Any<CancellationToken>());

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
    public async Task Inferred_signal_is_working_when_recent_waiting_with_pending_tool_and_stop_otherwise()
    {
        var recent = _time.GetUtcNow().AddSeconds(-2).ToString("O");
        File.WriteAllLines(Transcript("recent"), [User("recent", "go", recent), Assistant("recent", "m", TextBlock, ts: recent)]);
        File.WriteAllLines(Transcript("pending"), [Assistant("pending", "m", ToolBlock, ts: recent)]);
        File.WriteAllLines(Transcript("old"), [User("old", "go"), Assistant("old", "m", TextBlock)]);

        await _indexer.ScanAsync(CancellationToken.None);

        _updates.Single(u => u.SessionId == "recent").InferredSignal.ShouldBe(SessionSignal.ToolUse);
        _updates.Single(u => u.SessionId == "pending").InferredSignal.ShouldBe(SessionSignal.Notification);
        _updates.Single(u => u.SessionId == "old").InferredSignal.ShouldBe(SessionSignal.Stop);
    }
}
