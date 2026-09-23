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
        await _store.UpsertAsync([SessionRecord.FromSnapshot(Snapshot("s1", SessionState.Working, _now))]);
        await _store.UpsertAsync([SessionRecord.FromSnapshot(Snapshot("s1", SessionState.Idle, _now.AddMinutes(1)))]);

        var records = await _store.GetActiveSinceAsync(_now.AddHours(-1));

        var record = records.ShouldHaveSingleItem();
        record.State.ShouldBe(SessionState.Idle);
        record.LastEventAt.ShouldBe(_now.AddMinutes(1));
        record.ToSnapshot().LatestContext.ShouldBe(new TokenUsage(1, 2, 3, 4));
    }

    [Fact]
    public async Task GetActiveSince_filters_on_last_event_time()
    {
        await _store.UpsertAsync(
        [
            SessionRecord.FromSnapshot(Snapshot("old", SessionState.Ended, _now.AddDays(-3))),
            SessionRecord.FromSnapshot(Snapshot("new", SessionState.Idle, _now)),
        ]);

        (await _store.GetActiveSinceAsync(_now.AddDays(-1))).Select(r => r.Id).ShouldBe(["new"]);
    }

    [Fact]
    public async Task Events_append_query_newest_first_and_prune()
    {
        await _store.AppendEventsAsync(
        [
            new SessionEventRecord { SessionId = "s1", Kind = "PreToolUse", ToolName = "Bash", At = _now.AddDays(-20) },
            new SessionEventRecord { SessionId = "s1", Kind = "Stop", At = _now },
            new SessionEventRecord { SessionId = "s2", Kind = "Stop", At = _now },
        ]);

        var events = await _store.GetEventsAsync("s1", limit: 10);
        events.Select(e => e.Kind).ShouldBe(["Stop", "PreToolUse"]);

        (await _store.PruneEventsAsync(_now.AddDays(-14))).ShouldBe(1);
        (await _store.GetEventsAsync("s1", limit: 10)).Count.ShouldBe(1);
    }
}
