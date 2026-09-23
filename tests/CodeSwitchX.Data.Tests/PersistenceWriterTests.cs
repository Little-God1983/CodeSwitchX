using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.Data.Tests;

public class PersistenceWriterTests : IAsyncLifetime
{
    private readonly SqliteFixture _db = new();
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
    private PersistenceWriter _writer = null!;

    public async ValueTask InitializeAsync()
    {
        await _db.InitializeAsync();
        _writer = new PersistenceWriter(_bus, _db.Get<ISessionStore>(), _db.Get<IUsageStore>(), _time,
            NullLogger<PersistenceWriter>.Instance, new PersistenceWriterOptions());
        _writer.Subscribe();
    }

    public async ValueTask DisposeAsync()
    {
        _writer.Dispose();
        await _db.DisposeAsync();
    }

    private SessionSnapshot Snapshot(string id, SessionState state) => new()
    {
        SessionId = id, State = state, StartedAt = _time.GetUtcNow(), LastEventAt = _time.GetUtcNow(), StateSince = _time.GetUtcNow(),
    };

    [Fact]
    public async Task Session_changes_are_upserted_with_the_latest_snapshot_winning()
    {
        _bus.Publish(new SessionChanged(null, Snapshot("s1", SessionState.Starting)));
        _bus.Publish(new SessionChanged(null, Snapshot("s1", SessionState.Working)));
        _writer.Pending.ShouldBe(2);

        await _writer.FlushAsync(CancellationToken.None);

        var records = await _db.Get<ISessionStore>().GetActiveSinceAsync(_time.GetUtcNow().AddHours(-1), TestContext.Current.CancellationToken);
        records.ShouldHaveSingleItem().State.ShouldBe(SessionState.Working);
        _writer.Pending.ShouldBe(0);
    }

    [Fact]
    public async Task Hook_events_are_appended_without_payload_by_default()
    {
        _bus.Publish(new HookEventReceived(new HookEvent
        {
            SessionId = "s1", EventName = "PreToolUse", Signal = SessionSignal.ToolUse, At = _time.GetUtcNow(), ToolName = "Edit", RawJson = "{\"secret\":1}",
        }));

        await _writer.FlushAsync(CancellationToken.None);

        var stored = (await _db.Get<ISessionStore>().GetEventsAsync("s1", 10, TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        stored.Kind.ShouldBe("PreToolUse");
        stored.ToolName.ShouldBe("Edit");
        stored.PayloadJson.ShouldBeNull();
    }

    [Fact]
    public async Task Usage_deltas_in_the_same_minute_collapse_into_one_bucket()
    {
        var at = _time.GetUtcNow();
        _bus.Publish(new TranscriptUpdated(new TranscriptUpdate
        {
            SessionId = "s1", TranscriptPath = "p", ObservedAt = at,
            Usage =
            [
                new UsageDelta("claude-sonnet-5", at.AddSeconds(5), new TokenUsage(10, 1, 0, 100)),
                new UsageDelta("claude-sonnet-5", at.AddSeconds(40), new TokenUsage(20, 2, 5, 200)),
                new UsageDelta("claude-sonnet-5", at.AddMinutes(1), new TokenUsage(1, 1, 1, 1)),
            ],
        }));

        await _writer.FlushAsync(CancellationToken.None);

        var buckets = await _db.Get<IUsageStore>().GetBucketsAsync(at, at.AddMinutes(5), TestContext.Current.CancellationToken);
        buckets.Count.ShouldBe(2);
        buckets[0].Input.ShouldBe(30);
        buckets[0].CacheRead.ShouldBe(300);
        buckets[1].Input.ShouldBe(1);
    }

    [Fact]
    public async Task The_background_loop_flushes_on_the_timer()
    {
        using var cts = new CancellationTokenSource();
        await _writer.StartAsync(cts.Token);
        _bus.Publish(new SessionChanged(null, Snapshot("s1", SessionState.Idle)));

        _time.Advance(TimeSpan.FromMilliseconds(300));
        await WaitUntilAsync(() => _writer.Pending == 0);

        (await _db.Get<ISessionStore>().GetActiveSinceAsync(_time.GetUtcNow().AddHours(-1), TestContext.Current.CancellationToken)).Count.ShouldBe(1);
        await _writer.StopAsync(CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        condition().ShouldBeTrue();
    }

    [Fact]
    public async Task Usage_and_its_transcript_cursor_are_committed_in_one_store_call()
    {
        var usage = Substitute.For<IUsageStore>();
        using var writer = new PersistenceWriter(_bus, _db.Get<ISessionStore>(), usage, _time, NullLogger<PersistenceWriter>.Instance, new PersistenceWriterOptions());
        writer.Subscribe();
        var at = _time.GetUtcNow();
        var cursor = new TranscriptCursor { Path = @"c:\t\s1.jsonl", ByteOffset = 512, LastWriteUtc = at, SessionId = "s1" };
        _bus.Publish(new TranscriptUpdated(new TranscriptUpdate
        {
            SessionId = "s1", TranscriptPath = cursor.Path, ObservedAt = at, Cursor = cursor,
            Usage = [new UsageDelta("claude-sonnet-5", at, new TokenUsage(10, 1, 0, 100))],
            MessageIds = ["msg_1"],
        }));

        await writer.FlushAsync(CancellationToken.None);

        await usage.Received(1).CommitAsync(
            Arg.Is<IReadOnlyCollection<UsageBucket>>(b => b.Single().Input == 10),
            Arg.Is<IReadOnlyCollection<TranscriptCursor>>(c => c.Single().ByteOffset == 512),
            Arg.Is<IReadOnlyCollection<string>>(m => m.Single() == "msg_1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_batch_is_retried_on_the_next_flush_instead_of_being_dropped()
    {
        var usage = Substitute.For<IUsageStore>();
        usage.CommitAsync(Arg.Any<IReadOnlyCollection<UsageBucket>>(), Arg.Any<IReadOnlyCollection<TranscriptCursor>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("database is locked"), _ => Task.CompletedTask);
        using var writer = new PersistenceWriter(_bus, _db.Get<ISessionStore>(), usage, _time, NullLogger<PersistenceWriter>.Instance, new PersistenceWriterOptions());
        writer.Subscribe();
        var at = _time.GetUtcNow();
        _bus.Publish(new TranscriptUpdated(new TranscriptUpdate
        {
            SessionId = "s1", TranscriptPath = "p", ObservedAt = at,
            Usage = [new UsageDelta("claude-sonnet-5", at, new TokenUsage(10, 1, 0, 100))],
        }));

        await writer.FlushAsync(CancellationToken.None);
        await writer.FlushAsync(CancellationToken.None);

        await usage.Received(2).CommitAsync(
            Arg.Is<IReadOnlyCollection<UsageBucket>>(b => b.Single().Input == 10),
            Arg.Any<IReadOnlyCollection<TranscriptCursor>>(),
            Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Old_hook_events_are_pruned_once_per_hour()
    {
        var sessions = Substitute.For<ISessionStore>();
        var usage = Substitute.For<IUsageStore>();
        using var writer = new PersistenceWriter(_bus, sessions, usage, _time, NullLogger<PersistenceWriter>.Instance, new PersistenceWriterOptions());
        writer.Subscribe();

        await writer.FlushAsync(CancellationToken.None);
        await writer.FlushAsync(CancellationToken.None);
        await sessions.Received(1).PruneEventsAsync(_time.GetUtcNow() - PersistenceWriterOptions.DefaultEventRetention, Arg.Any<CancellationToken>());
        await usage.Received(1).PruneSeenMessagesAsync(PersistenceWriterOptions.DefaultSeenMessageIdsKept, Arg.Any<CancellationToken>());

        _time.Advance(TimeSpan.FromMinutes(61));
        await writer.FlushAsync(CancellationToken.None);

        await sessions.Received(2).PruneEventsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stopping_the_writer_persists_every_queued_item_not_just_one_batch()
    {
        using var writer = new PersistenceWriter(_bus, _db.Get<ISessionStore>(), _db.Get<IUsageStore>(), _time, NullLogger<PersistenceWriter>.Instance,
            new PersistenceWriterOptions { MaxBatch = 2 });
        await writer.StartAsync(CancellationToken.None);
        for (var i = 0; i < 5; i++)
        {
            _bus.Publish(new SessionChanged(null, Snapshot("s" + i, SessionState.Idle)));
        }

        await writer.StopAsync(CancellationToken.None);

        (await _db.Get<ISessionStore>().GetActiveSinceAsync(_time.GetUtcNow().AddHours(-1), TestContext.Current.CancellationToken)).Count.ShouldBe(5);
    }

    [Fact]
    public async Task A_batch_interrupted_by_cancellation_is_kept_for_the_next_flush()
    {
        var usage = Substitute.For<IUsageStore>();
        usage.CommitAsync(Arg.Any<IReadOnlyCollection<UsageBucket>>(), Arg.Any<IReadOnlyCollection<TranscriptCursor>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new OperationCanceledException(), _ => Task.CompletedTask);
        using var writer = new PersistenceWriter(_bus, _db.Get<ISessionStore>(), usage, _time, NullLogger<PersistenceWriter>.Instance, new PersistenceWriterOptions());
        writer.Subscribe();
        var at = _time.GetUtcNow();
        _bus.Publish(new TranscriptUpdated(new TranscriptUpdate
        {
            SessionId = "s1", TranscriptPath = "p", ObservedAt = at,
            Usage = [new UsageDelta("claude-sonnet-5", at, new TokenUsage(10, 1, 0, 100))],
        }));

        await Should.ThrowAsync<OperationCanceledException>(() => writer.FlushAsync(CancellationToken.None));
        await writer.FlushAsync(CancellationToken.None);

        await usage.Received(2).CommitAsync(
            Arg.Is<IReadOnlyCollection<UsageBucket>>(b => b.Single().Input == 10),
            Arg.Any<IReadOnlyCollection<TranscriptCursor>>(),
            Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<CancellationToken>());
    }
}
