using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Data.Tests;

public class SessionStoreTests : IAsyncLifetime
{
    private readonly SqliteFixture _db = new();
    private ISessionStore _store = null!;
    private readonly DateTimeOffset _now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync()
    {
        await _db.InitializeAsync();
        _store = _db.Get<ISessionStore>();
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    private SessionSnapshot Snapshot(string id, SessionState state, DateTimeOffset lastEvent) => new()
    {
        SessionId = id,
        State = state,
        StartedAt = lastEvent.AddMinutes(-10),
        LastEventAt = lastEvent,
        StateSince = lastEvent,
        Title = "Title " + id,
        Model = "claude-sonnet-5",
        LatestContext = new TokenUsage(1, 2, 3, 4),
    };

    [Fact]
    public async Task Upsert_inserts_then_updates_by_session_id()
    {
        await _store.UpsertAsync([SessionRecord.FromSnapshot(Snapshot("s1", SessionState.Working, _now))], TestContext.Current.CancellationToken);
        await _store.UpsertAsync([SessionRecord.FromSnapshot(Snapshot("s1", SessionState.Idle, _now.AddMinutes(1)))], TestContext.Current.CancellationToken);

        var records = await _store.GetActiveSinceAsync(_now.AddHours(-1), TestContext.Current.CancellationToken);

        var record = records.ShouldHaveSingleItem();
        record.State.ShouldBe(SessionState.Idle);
        record.LastEventAt.ShouldBe(_now.AddMinutes(1));
        record.ToSnapshot().LatestContext.ShouldBe(new TokenUsage(1, 2, 3, 4));
    }

    /// <summary>A chat quiet for longer than the restore window is not restored, so when it becomes active again the engine starts it over.</summary>
    private SessionSnapshot FreshStart(string id, string? title) => new()
    {
        SessionId = id,
        State = SessionState.Working,
        StartedAt = _now,
        LastEventAt = _now,
        StateSince = _now,
        Title = title,
    };

    [Fact]
    public async Task Upsert_keeps_a_renamed_title_and_the_first_start_when_the_chat_comes_back_as_a_fresh_snapshot()
    {
        var renamed = Snapshot("s1", SessionState.Idle, _now.AddDays(-2)) with { Title = "Auth refactor", TitleLocked = true };
        await _store.UpsertAsync([SessionRecord.FromSnapshot(renamed)], TestContext.Current.CancellationToken);

        await _store.UpsertAsync([SessionRecord.FromSnapshot(FreshStart("s1", "fix the login bug"))], TestContext.Current.CancellationToken);

        var record = (await _store.GetActiveSinceAsync(_now.AddHours(-1), TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        record.Title.ShouldBe("Auth refactor");
        record.TitleLocked.ShouldBeTrue();
        record.StartedAt.ShouldBe(renamed.StartedAt);
        record.State.ShouldBe(SessionState.Working, "the rest of the row still follows the live chat");
        record.LastEventAt.ShouldBe(_now);
    }

    [Fact]
    public async Task Upsert_keeps_the_stored_title_when_a_fresh_snapshot_has_none_yet()
    {
        await _store.UpsertAsync([SessionRecord.FromSnapshot(Snapshot("s1", SessionState.Idle, _now.AddDays(-2)))], TestContext.Current.CancellationToken);

        await _store.UpsertAsync([SessionRecord.FromSnapshot(FreshStart("s1", title: null))], TestContext.Current.CancellationToken);

        (await _store.GetActiveSinceAsync(_now.AddHours(-1), TestContext.Current.CancellationToken)).ShouldHaveSingleItem().Title.ShouldBe("Title s1");
    }

    [Fact]
    public async Task Upsert_takes_a_newer_title_unless_only_the_stored_one_is_a_rename()
    {
        await _store.UpsertAsync([
            SessionRecord.FromSnapshot(Snapshot("auto", SessionState.Idle, _now)),
            SessionRecord.FromSnapshot(Snapshot("renamed", SessionState.Idle, _now) with { Title = "Old name", TitleLocked = true }),
        ], TestContext.Current.CancellationToken);

        await _store.UpsertAsync([
            SessionRecord.FromSnapshot(Snapshot("auto", SessionState.Idle, _now) with { Title = "Newer summary" }),
            SessionRecord.FromSnapshot(Snapshot("renamed", SessionState.Idle, _now) with { Title = "New name", TitleLocked = true }),
        ], TestContext.Current.CancellationToken);

        var titles = (await _store.GetActiveSinceAsync(_now.AddHours(-1), TestContext.Current.CancellationToken)).ToDictionary(r => r.Id, r => r.Title);
        titles["auto"].ShouldBe("Newer summary");
        titles["renamed"].ShouldBe("New name");
    }

    [Fact]
    public async Task GetActiveSince_filters_on_last_event_time()
    {
        await _store.UpsertAsync([
            SessionRecord.FromSnapshot(Snapshot("old", SessionState.Ended, _now.AddDays(-3))),
            SessionRecord.FromSnapshot(Snapshot("new", SessionState.Idle, _now)),
        ], TestContext.Current.CancellationToken);

        (await _store.GetActiveSinceAsync(_now.AddDays(-1), TestContext.Current.CancellationToken)).Select(r => r.Id).ShouldBe(["new"]);
    }

    [Fact]
    public async Task Events_append_query_newest_first_and_prune()
    {
        await _store.AppendEventsAsync([
            new SessionEventRecord { SessionId = "s1", Kind = "PreToolUse", ToolName = "Bash", At = _now.AddDays(-20) },
            new SessionEventRecord { SessionId = "s1", Kind = "Stop", At = _now },
            new SessionEventRecord { SessionId = "s2", Kind = "Stop", At = _now },
        ], TestContext.Current.CancellationToken);

        var events = await _store.GetEventsAsync("s1", limit: 10, ct: TestContext.Current.CancellationToken);
        events.Select(e => e.Kind).ShouldBe(["Stop", "PreToolUse"]);

        (await _store.PruneEventsAsync(_now.AddDays(-14), TestContext.Current.CancellationToken)).ShouldBe(1);
        (await _store.GetEventsAsync("s1", limit: 10, ct: TestContext.Current.CancellationToken)).Count.ShouldBe(1);
    }
}
