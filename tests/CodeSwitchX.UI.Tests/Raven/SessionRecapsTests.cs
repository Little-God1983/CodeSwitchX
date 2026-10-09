using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Raven;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Raven;

public sealed class SessionRecapsTests : IDisposable
{
    private static readonly Guid Workspace = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Afternoon = new(2026, 10, 8, 14, 0, 0, TimeSpan.Zero); // a Thursday

    private readonly string _path = Path.Combine(Path.GetTempPath(), "csx-recap-" + Guid.NewGuid().ToString("N") + ".jsonl");
    private readonly FakeBrain _brain = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.Zero));
    private readonly IUsageStore _usage = Substitute.For<IUsageStore>();
    private readonly ISessionStore _stored = Substitute.For<ISessionStore>();
    private readonly IYardDirectory _yard = Substitute.For<IYardDirectory>();
    private readonly List<(string Folder, string Arguments)> _git = [];
    private readonly List<SessionSnapshot> _sessions = [];
    private readonly SessionRecaps _recaps;

    public SessionRecapsTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        File.WriteAllLines(_path, [
            """{"timestamp":"2026-10-08T14:01:00.000Z","type":"user","isSidechain":false,"message":{"role":"user","content":"Fix the upload retry."}}""",
            """{"timestamp":"2026-10-08T14:20:00.000Z","type":"assistant","isSidechain":false,"message":{"role":"assistant","content":[{"type":"text","text":"Fixed; tests pass."}]}}""",
        ]);
        _sessions.Add(Chat("a", "Fix the upload", _path));
        _yard.WorkspacesAsync(Arg.Any<CancellationToken>()).Returns([new YardWorkspace(Workspace, "ContentAutomatorX", "Apps", @"E:\Repos\CA", [], [])]);
        _usage.GetBucketsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(
            [.. Minutes("a", Afternoon, 30), .. Minutes("raven", Afternoon, 5)]);
        _stored.GetActiveSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([]);
        _recaps = new SessionRecaps(_brain, _usage, _stored, () => _sessions, _yard, (folder, arguments, _) =>
        {
            _git.Add((folder, arguments));
            return Task.FromResult<string?>(arguments.StartsWith("config", StringComparison.Ordinal) ? "me@example.com\n" : "Fix upload retry\nAdd retry tests\n");
        }, _time, NullLogger<SessionRecaps>.Instance);
    }

    public void Dispose() => File.Delete(_path);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SessionSnapshot Chat(string id, string title, string path) => new()
    {
        SessionId = id, WorkspaceId = Workspace, Title = title, TranscriptPath = path, State = SessionState.Idle,
        StartedAt = Afternoon, LastEventAt = Afternoon, StateSince = Afternoon,
    };

    private static IEnumerable<UsageBucket> Minutes(string chat, DateTimeOffset from, int count) =>
        Enumerable.Range(0, count).Select(i => new UsageBucket { SessionId = chat, Model = "m", MinuteUtc = from.AddMinutes(i), Output = 1 });

    [Fact]
    public async Task The_recapper_is_told_when_the_session_was_what_each_chat_did_and_the_commits()
    {
        (await _recaps.QuestionAsync(Ct)).ShouldBe("It was yesterday afternoon (Thursday 8 October), from 14:00 to 14:30.\n\n"
            + "Chat \"Fix the upload\" in ContentAutomatorX, 30 minutes of work:\nUser: Fix the upload retry.\nClaude: Fixed; tests pass.\n\n"
            + "Commits made then:\nContentAutomatorX: Fix upload retry; Add retry tests");

        _git.ShouldBe([(@"E:\Repos\CA", "config user.email"), (@"E:\Repos\CA",
            "log --branches --no-merges --author=\"me@example.com\" --since=\"2026-10-08 14:00:00 +0000\" --until=\"2026-10-08 14:45:00 +0000\" "
            + "--pretty=format:%s -n 15")], "the user's own commits on local branches, a little past the last minute of work");
    }

    /// <summary>After a restart the app keeps in mind only the chats of the day before: the saved ones of the look-back count too.</summary>
    [Fact]
    public async Task A_chat_only_saved_counts_with_its_title_and_conversation()
    {
        _sessions.Clear();
        _stored.GetActiveSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(
            [new SessionRecord { Id = "a", WorkspaceId = Workspace, Title = "Fix the upload (saved)", TranscriptPath = _path }]);

        (await _recaps.QuestionAsync(Ct)).ShouldContain("Chat \"Fix the upload (saved)\" in ContentAutomatorX, 30 minutes of work:");
    }

    [Fact]
    public async Task Times_are_the_user_s_own()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("Plus two", TimeSpan.FromHours(2), "Plus two", "Plus two"));

        (await _recaps.QuestionAsync(Ct)).ShouldStartWith("It was yesterday afternoon (Thursday 8 October), from 16:00 to 16:30.");
    }

    [Fact]
    public async Task The_summary_is_one_short_text()
    {
        _brain.Answer = _ => [new BrainText("Yesterday afternoon the upload retry in ContentAutomatorX got fixed and committed.\n"),
            new BrainText("Nothing waits on you.")];

        (await _recaps.RecapAsync(Ct)).ShouldBe("Yesterday afternoon the upload retry in ContentAutomatorX got fixed and committed. Nothing waits on you.");
    }

    [Fact]
    public async Task With_no_session_before_this_one_it_says_so()
    {
        _usage.GetBucketsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(
            [.. Minutes("a", _time.GetUtcNow().AddMinutes(-20), 10)]);

        (await Should.ThrowAsync<YardActionException>(() => _recaps.RecapAsync(Ct))).Message
            .ShouldBe("No working session before this one shows in the last 14 days.");
    }

    [Theory]
    [InlineData(2026, 10, 9, 7, "earlier today, in the morning")]
    [InlineData(2026, 10, 8, 23, "last night (Thursday 8 October)")]
    [InlineData(2026, 10, 9, 2, "last night (Thursday 8 October)")]
    [InlineData(2026, 10, 8, 1, "on Wednesday night (7 October)")]
    [InlineData(2026, 10, 5, 19, "on Monday evening (5 October)")]
    [InlineData(2026, 9, 28, 10, "on Monday 28 September, in the morning")]
    public void When_is_said_as_a_person_would(int year, int month, int day, int hour, string said) =>
        _recaps.When(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero), _time.GetUtcNow()).ShouldBe(said);
}
