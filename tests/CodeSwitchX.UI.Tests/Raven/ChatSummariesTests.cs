using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Raven;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.UI.Tests.Raven;

public sealed class ChatSummariesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "csx-sum-" + Guid.NewGuid().ToString("N") + ".jsonl");
    private readonly FakeBrain _brain = new();
    private readonly FakeTimeProvider _time = new();
    private readonly ChatSummaries _summaries;

    public ChatSummariesTests()
    {
        File.WriteAllLines(_path, [
            """{"type":"user","isSidechain":false,"message":{"role":"user","content":"Fix the upload retry."}}""",
            """{"type":"assistant","isSidechain":false,"message":{"role":"assistant","content":[{"type":"text","text":"Done: the retry backs off."}]}}""",
        ]);
        _summaries = new ChatSummaries(_brain, id => id == "a" ? _path : null, _time, NullLogger<ChatSummaries>.Instance);
    }

    public void Dispose() => File.Delete(_path);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static YardChat Chat(string id, SessionState state = SessionState.Idle, bool needsYou = false) =>
        new(id, "Fix the upload", Guid.Empty, "ContentAutomatorX", state, needsYou, DateTimeOffset.UnixEpoch, "1m", null, null, 0, null, null);

    [Fact]
    public async Task The_summarizer_is_given_the_conversation_and_its_short_part_is_said()
    {
        _brain.Answer = _ => [new BrainText("Short: It fixed the upload retry, and the tests pass. It waits for nothing.\n"),
            new BrainText("Asked: Fix the upload retry.\nDone: Changed src/Upload.cs.\nNow: Idle.\nWaiting: nothing")];

        var summary = await _summaries.SummarizeAsync(Chat("a"), Ct);

        summary.Short.ShouldBe("It fixed the upload retry, and the tests pass. It waits for nothing.");
        summary.Full.ShouldBe("Asked: Fix the upload retry.\nDone: Changed src/Upload.cs.\nNow: Idle.\nWaiting: nothing");
        _brain.Asked.ShouldHaveSingleItem().ShouldBe("The chat \"Fix the upload\" in ContentAutomatorX, idle, its turn over.\n\n"
            + "Its conversation, in short:\nUser: Fix the upload retry.\nClaude: Done: the retry backs off.");
    }

    [Theory]
    [InlineData("Short:\nIt fixed it.\nAsked: Fix it.", "It fixed it.", "Asked: Fix it.")]
    [InlineData("```\n1. Short: It fixed it.\n2. Asked: Fix it.\n```", "It fixed it.", "Asked: Fix it.")]
    [InlineData("Here is the summary.\n- **Short:** It fixed it.\n- Waiting: nothing", "It fixed it.", "Here is the summary.\nWaiting: nothing")]
    [InlineData("Short:\nAsked: Fix it.\nWaiting: nothing", "Asked: Fix it. Waiting: nothing", "Asked: Fix it.\nWaiting: nothing")]
    [InlineData("Short: 1.5 MB of logs were cut.\n1. Done: Cut 1.5 MB.", "1.5 MB of logs were cut.", "Done: Cut 1.5 MB.")]
    public void Shapes_a_model_may_add_are_read(string reply, string said, string written)
    {
        ChatSummaries.Parse(reply).ShouldBe(new ChatSummary(said, written));
    }

    [Fact]
    public async Task A_summary_waiting_behind_another_too_long_says_so()
    {
        _brain.Gate = new TaskCompletionSource();
        var first = _summaries.SummarizeAsync(Chat("a"), Ct);
        var second = _summaries.SummarizeAsync(Chat("a"), Ct);

        _time.Advance(ChatSummaries.Limit);

        (await Should.ThrowAsync<YardActionException>(() => second)).Message
            .ShouldBe("Summing up the Fix the upload chat took longer than 60 seconds and was given up.");
        (await Should.ThrowAsync<YardActionException>(() => first)).Message.ShouldEndWith("was given up.");
    }

    [Fact]
    public void A_reply_out_of_shape_is_said_and_written_whole()
    {
        ChatSummaries.Parse("**It fixed it.**\nAll good.").ShouldBe(new ChatSummary("It fixed it. All good.", "It fixed it.\nAll good."));
        ChatSummaries.Parse("  ").ShouldBeNull();
    }

    [Fact]
    public async Task A_chat_with_no_conversation_or_a_failed_summarizer_says_why()
    {
        (await Should.ThrowAsync<YardActionException>(() => _summaries.SummarizeAsync(Chat("b"), Ct))).Message
            .ShouldBe("CodeSwitchX cannot read the conversation of the Fix the upload chat, so it cannot sum it up.");

        _brain.Answer = _ => [new BrainFailed("Claude Code is not installed.")];
        (await Should.ThrowAsync<YardActionException>(() => _summaries.SummarizeAsync(Chat("a", SessionState.Waiting, needsYou: true), Ct))).Message
            .ShouldBe("Summing up the Fix the upload chat failed: Claude Code is not installed.");
        _brain.Asked[^1].ShouldStartWith("The chat \"Fix the upload\" in ContentAutomatorX, waiting on the user right now.");
    }
}
