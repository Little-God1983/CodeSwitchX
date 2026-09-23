using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

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

        var records = await _db.Get<ISessionStore>().GetActiveSinceAsync(_time.GetUtcNow().AddHours(-1));
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

        var stored = (await _db.Get<ISessionStore>().GetEventsAsync("s1", 10)).ShouldHaveSingleItem();
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

        var buckets = await _db.Get<IUsageStore>().GetBucketsAsync(at, at.AddMinutes(5));
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

        (await _db.Get<ISessionStore>().GetActiveSinceAsync(_time.GetUtcNow().AddHours(-1))).Count.ShouldBe(1);
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
}
