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
        LatestContext = new TokenUsage(1, 2, 3, 4, 0),
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
        record.ToSnapshot().LatestContext.ShouldBe(new TokenUsage(1, 2, 3, 4, 0));
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

    /// <summary>#276: a chat's start and end are no use of its window; a chat whose events are gone goes by its last event.</summary>
    [Fact]
    public async Task Last_use_by_workspace_goes_by_the_events_of_use_and_leaves_out_starts_ends_and_sessions_without_a_conversation()
    {
        Guid app = Guid.NewGuid(), shop = Guid.NewGuid(), docs = Guid.NewGuid(), ghostly = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        await _store.UpsertAsync([
            // Used two days ago, its window closed (SessionEnd) just now.
            SessionRecord.FromSnapshot(Snapshot("a1", SessionState.Ended, _now) with { WorkspaceId = app }),
            SessionRecord.FromSnapshot(Snapshot("a2", SessionState.Idle, _now.AddDays(-5)) with { WorkspaceId = app }),
            // Only started again (a window reload) since its events were pruned: no use.
            SessionRecord.FromSnapshot(Snapshot("s1", SessionState.Idle, _now) with { WorkspaceId = shop }),
            // No events kept: its last event.
            SessionRecord.FromSnapshot(Snapshot("d1", SessionState.Ended, _now.AddDays(-30)) with { WorkspaceId = docs }),
            // Never held a conversation.
            SessionRecord.FromSnapshot(Snapshot("g1", SessionState.Ended, _now) with { WorkspaceId = ghostly, Title = null, LatestContext = TokenUsage.Zero }),
        ], ct);
        await _store.AppendEventsAsync([
            new SessionEventRecord { SessionId = "a1", Kind = "UserPromptSubmit", At = _now.AddDays(-2).AddMinutes(-5) },
            new SessionEventRecord { SessionId = "a1", Kind = "Stop", At = _now.AddDays(-2) },
            new SessionEventRecord { SessionId = "a1", Kind = "SessionEnd", At = _now },
            new SessionEventRecord { SessionId = "s1", Kind = "SessionStart", At = _now },
            new SessionEventRecord { SessionId = "g1", Kind = "Stop", At = _now },
        ], ct);

        var lastUse = await _store.GetLastUseByWorkspaceAsync(ct);

        lastUse.Count.ShouldBe(2);
        lastUse[app].ShouldBe(_now.AddDays(-2).AddMinutes(-5), "the prompt; the turn's end and the chat's end are no use, and the other chat's last event, five days ago, is older");
        lastUse[docs].ShouldBe(_now.AddDays(-30));
    }

    /// <summary>#278: the stored use is the one the Yard saw live; only a row from before it goes by its events.</summary>
    [Fact]
    public async Task Last_use_by_workspace_goes_by_the_stored_last_use_where_a_row_has_one()
    {
        Guid app = Guid.NewGuid(), shop = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        await _store.UpsertAsync([
            // Used three hours ago, reloaded just now; its use events were pruned.
            SessionRecord.FromSnapshot(Snapshot("a1", SessionState.Idle, _now) with { WorkspaceId = app, LastUseAt = _now.AddHours(-3) }),
            // Stored before #278 and not used since: its prompt.
            SessionRecord.FromSnapshot(Snapshot("s1", SessionState.Idle, _now) with { WorkspaceId = shop }),
        ], ct);
        await _store.AppendEventsAsync([
            new SessionEventRecord { SessionId = "a1", Kind = "SessionStart", At = _now },
            new SessionEventRecord { SessionId = "s1", Kind = "UserPromptSubmit", At = _now.AddDays(-1) },
            new SessionEventRecord { SessionId = "s1", Kind = "SessionStart", At = _now },
        ], ct);

        var lastUse = await _store.GetLastUseByWorkspaceAsync(ct);

        lastUse[app].ShouldBe(_now.AddHours(-3));
        lastUse[shop].ShouldBe(_now.AddDays(-1));
    }

    [Fact]
    public async Task Upsert_keeps_the_later_last_use_when_the_chat_comes_back_as_a_fresh_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.UpsertAsync([SessionRecord.FromSnapshot(Snapshot("s1", SessionState.Idle, _now.AddDays(-2)) with { LastUseAt = _now.AddDays(-2) })], ct);

        await _store.UpsertAsync([SessionRecord.FromSnapshot(FreshStart("s1", title: null) with { State = SessionState.Idle })], ct);
        (await _store.GetActiveSinceAsync(_now.AddHours(-1), ct)).ShouldHaveSingleItem().LastUseAt.ShouldBe(_now.AddDays(-2), "a reload is no use");

        await _store.UpsertAsync([SessionRecord.FromSnapshot(FreshStart("s1", title: null) with { LastUseAt = _now })], ct);
        (await _store.GetActiveSinceAsync(_now.AddHours(-1), ct)).ShouldHaveSingleItem().LastUseAt.ShouldBe(_now);
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
