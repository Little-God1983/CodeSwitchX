using CodeSwitchX.Core.Persistence;

namespace CodeSwitchX.Data.Tests;

public class UsageStoreTests : IAsyncLifetime
{
    private readonly SqliteFixture _db = new();
    private IUsageStore _store = null!;
    private readonly DateTimeOffset _minute = new(2026, 9, 23, 12, 34, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync()
    {
        await _db.InitializeAsync();
        _store = _db.Get<IUsageStore>();
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    [Fact]
    public async Task Deltas_for_the_same_key_are_summed_into_one_bucket()
    {
        await _store.AddUsageAsync([new UsageBucket { SessionId = "s1", Model = "m", MinuteUtc = _minute, Input = 10, Output = 1, CacheWrite = 5, CacheRead = 100 }], TestContext.Current.CancellationToken);
        await _store.AddUsageAsync([new UsageBucket { SessionId = "s1", Model = "m", MinuteUtc = _minute, Input = 5, Output = 2, CacheWrite = 0, CacheRead = 50 }], TestContext.Current.CancellationToken);

        var bucket = (await _store.GetBucketsAsync(_minute, _minute.AddMinutes(1), TestContext.Current.CancellationToken)).ShouldHaveSingleItem();

        bucket.Input.ShouldBe(15);
        bucket.Output.ShouldBe(3);
        bucket.CacheWrite.ShouldBe(5);
        bucket.CacheRead.ShouldBe(150);
    }

    [Fact]
    public async Task Range_query_is_inclusive_of_from_and_exclusive_of_to()
    {
        await _store.AddUsageAsync([
            new UsageBucket { SessionId = "s1", Model = "m", MinuteUtc = _minute.AddMinutes(-1), Input = 1 },
            new UsageBucket { SessionId = "s1", Model = "m", MinuteUtc = _minute, Input = 2 },
            new UsageBucket { SessionId = "s1", Model = "m", MinuteUtc = _minute.AddMinutes(1), Input = 3 },
            new UsageBucket { SessionId = "s2", Model = "m", MinuteUtc = _minute, Input = 4 },
        ], TestContext.Current.CancellationToken);

        var buckets = await _store.GetBucketsAsync(_minute, _minute.AddMinutes(1), TestContext.Current.CancellationToken);

        buckets.Select(b => b.Input).ShouldBe([2, 4], ignoreOrder: true);
    }

    [Fact]
    public void FloorToMinute_drops_seconds_and_converts_to_utc()
    {
        var local = new DateTimeOffset(2026, 9, 23, 14, 34, 59, 999, TimeSpan.FromHours(2));

        UsageBucket.FloorToMinute(local).ShouldBe(_minute);
    }

    [Fact]
    public async Task Cursors_upsert_by_path()
    {
        await _store.UpsertCursorsAsync([new TranscriptCursor { Path = @"c:\t\s1.jsonl", ByteOffset = 10, LastWriteUtc = _minute, SessionId = "s1" }], TestContext.Current.CancellationToken);
        await _store.UpsertCursorsAsync([new TranscriptCursor { Path = @"c:\t\s1.jsonl", ByteOffset = 20, LastWriteUtc = _minute.AddSeconds(5), SessionId = "s1" }], TestContext.Current.CancellationToken);

        var cursor = (await _store.GetCursorsAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        cursor.ByteOffset.ShouldBe(20);
        cursor.LastWriteUtc.ShouldBe(_minute.AddSeconds(5));
    }

    [Fact]
    public async Task Commit_writes_usage_and_transcript_cursors_in_one_transaction()
    {
        await _store.CommitAsync([new UsageBucket { SessionId = "s1", Model = "m", MinuteUtc = _minute, Input = 10 }], [new TranscriptCursor { Path = @"c:\t\s1.jsonl", ByteOffset = 10, LastWriteUtc = _minute, SessionId = "s1" }], [], TestContext.Current.CancellationToken);
        await _store.CommitAsync([new UsageBucket { SessionId = "s1", Model = "m", MinuteUtc = _minute, Input = 5 }], [new TranscriptCursor { Path = @"c:\t\s1.jsonl", ByteOffset = 20, LastWriteUtc = _minute.AddSeconds(5), SessionId = "s1" }], [], TestContext.Current.CancellationToken);

        (await _store.GetBucketsAsync(_minute, _minute.AddMinutes(1), TestContext.Current.CancellationToken)).ShouldHaveSingleItem().Input.ShouldBe(15);
        (await _store.GetCursorsAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem().ByteOffset.ShouldBe(20);
    }

    [Fact]
    public async Task Seen_message_ids_are_committed_with_the_usage_and_come_back_oldest_first()
    {
        await _store.CommitAsync([], [], ["msg_a"], TestContext.Current.CancellationToken);
        await _store.CommitAsync([], [], ["msg_b", "msg_a"], TestContext.Current.CancellationToken);

        (await _store.GetSeenMessageIdsAsync(10, TestContext.Current.CancellationToken)).ShouldBe(["msg_a", "msg_b"]);
        (await _store.GetSeenMessageIdsAsync(1, TestContext.Current.CancellationToken)).ShouldBe(["msg_b"], "the limit keeps the most recent ids");
    }

    [Fact]
    public async Task A_commit_can_remember_more_message_ids_than_SQLite_allows_parameters_in_one_statement()
    {
        // SQLite allows at most 32,766 parameters per statement, and the first start over a large history remembers the
        // ids of up to 500 whole transcripts in one commit.
        var ids = Enumerable.Range(0, 40_000).Select(i => "msg_" + i).ToArray();

        await _store.CommitAsync([], [], ids, TestContext.Current.CancellationToken);
        await _store.CommitAsync([], [], [.. ids, "msg_new"], TestContext.Current.CancellationToken);

        var seen = await _store.GetSeenMessageIdsAsync(50_000, TestContext.Current.CancellationToken);
        seen.Count.ShouldBe(40_001, "the second commit only adds the id it had not seen");
        seen[^1].ShouldBe("msg_new");
    }

    [Fact]
    public async Task Pruning_seen_message_ids_keeps_only_the_newest()
    {
        await _store.CommitAsync([], [], ["a", "b", "c"], TestContext.Current.CancellationToken);

        await _store.PruneSeenMessagesAsync(2, TestContext.Current.CancellationToken);

        (await _store.GetSeenMessageIdsAsync(10, TestContext.Current.CancellationToken)).ShouldBe(["b", "c"]);
    }
}
