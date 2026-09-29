using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.Telemetry.Tests;

public class TelemetryServiceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 23, 12, 30, 0, TimeSpan.Zero));
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly IUsageStore _usage = Substitute.For<IUsageStore>();
    private readonly ISettingsStore _settings = Substitute.For<ISettingsStore>();

    public TelemetryServiceTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _settings.GetPricingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<PricingRule>>(
            [new PricingRule { Model = "claude-sonnet-5", InputPerM = 2m, OutputPerM = 10m, CacheWritePerM = 2.5m, CacheReadPerM = 0.2m, ContextWindow = 1_000_000 }]));
        _usage.GetBucketsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<UsageBucket>>(
            [new UsageBucket { SessionId = "old", Model = "claude-sonnet-5", MinuteUtc = _time.GetUtcNow().AddHours(-1), Input = 1_000_000 }]));
    }

    private TelemetryService Service() => new(_usage, _settings, _bus, _time, NullLogger<TelemetryService>.Instance);

    [Fact]
    public async Task Start_loads_recent_buckets()
    {
        var service = Service();

        await service.StartAsync(CancellationToken.None);

        await _usage.Received(1).GetBucketsAsync(_time.GetUtcNow().AddDays(-7), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        service.Current.Today.Tokens.Input.ShouldBe(1_000_000);
        service.Current.Today.Cost.ShouldBe(2m);
        service.Current.FiveHours.Tokens.Input.ShouldBe(1_000_000);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Start_does_not_copy_the_shipped_prices_into_the_database()
    {
        var service = Service();
        await service.StartAsync(CancellationToken.None);

        // A stored rule overrides the default for its model, so a stored copy of a default would outlive its correction.
        await _settings.DidNotReceive().UpsertPricingAsync(Arg.Any<IReadOnlyCollection<PricingRule>>(), Arg.Any<CancellationToken>());
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Two_overlapping_pricing_reloads_leave_the_rules_read_last_in_place()
    {
        var service = Service();
        await service.StartAsync(CancellationToken.None);
        var firstRead = new TaskCompletionSource<IReadOnlyList<PricingRule>>();
        var reads = 0;
        _settings.GetPricingAsync(Arg.Any<CancellationToken>()).Returns(_ => ++reads == 1
            ? firstRead.Task
            : Task.FromResult<IReadOnlyList<PricingRule>>([new PricingRule { Model = "claude-sonnet-5", InputPerM = 4m, OutputPerM = 10m }]));

        var first = service.ReloadPricingAsync(CancellationToken.None);
        var second = service.ReloadPricingAsync(CancellationToken.None); // started after the user's rule was saved
        firstRead.SetResult([new PricingRule { Model = "claude-sonnet-5", InputPerM = 2m, OutputPerM = 10m }]); // the older rules arrive last
        await Task.WhenAll(first, second);

        service.Pricing.Find("claude-sonnet-5").InputPerM.ShouldBe(4m);
        service.Current.Today.Cost.ShouldBe(4m);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Transcript_usage_updates_the_snapshot_and_publishes()
    {
        var service = Service();
        await service.StartAsync(CancellationToken.None);
        TelemetrySnapshot? published = null;
        _bus.Subscribe<TelemetryUpdated>(m => published = m.Snapshot);

        _bus.Publish(new TranscriptUpdated(new TranscriptUpdate
        {
            SessionId = "s1", TranscriptPath = "p", ObservedAt = _time.GetUtcNow(),
            Usage = [new UsageDelta("claude-sonnet-5", _time.GetUtcNow(), new TokenUsage(500_000, 100_000, 0, 0))],
        }));

        published.ShouldNotBeNull();
        published.Today.Tokens.Input.ShouldBe(1_500_000);
        published.Today.Tokens.Output.ShouldBe(100_000);
        published.Today.Cost.ShouldBe(2m + 1m + 1m);
        published.RatePerMinute.Length.ShouldBe(60);
        published.RatePerMinute[^1].ShouldBe(600_000);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_handler_of_the_snapshot_holds_no_transcript_update_back_and_the_newest_totals_still_come_last()
    {
        var service = Service();
        await service.StartAsync(CancellationToken.None);
        var published = new List<TelemetrySnapshot>();
        var updateWentThrough = false;
        _bus.Subscribe<TelemetryUpdated>(m =>
        {
            lock (published)
            {
                published.Add(m.Snapshot);
            }

            if (published.Count > 1)
            {
                return;
            }

            // The indexer's thread brings usage while the tick's handler is still running (a UI marshal, say).
            var update = Task.Run(() => _bus.Publish(new TranscriptUpdated(new TranscriptUpdate
            {
                SessionId = "s1",
                TranscriptPath = "p",
                ObservedAt = _time.GetUtcNow(),
                Usage = [new UsageDelta("claude-sonnet-5", _time.GetUtcNow(), new TokenUsage(500, 0, 0, 0))],
            })));
            updateWentThrough = update.Wait(TimeSpan.FromSeconds(10));
        });

        _time.Advance(TelemetryService.RefreshInterval);

        updateWentThrough.ShouldBeTrue("the update must not wait behind a handler of the published snapshot");
        service.Current.Today.Tokens.Input.ShouldBe(1_000_500);
        published[^1].Today.Tokens.Input.ShouldBe(1_000_500, "the snapshot published last is the newest");
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Pricing_reload_picks_up_user_edits()
    {
        var service = Service();
        await service.StartAsync(CancellationToken.None);
        _settings.GetPricingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<PricingRule>>(
            [new PricingRule { Model = "claude-sonnet-5", InputPerM = 4m, OutputPerM = 10m }]));

        await service.ReloadPricingAsync(CancellationToken.None);

        service.Pricing.Find("claude-sonnet-5").InputPerM.ShouldBe(4m);
        service.Current.Today.Cost.ShouldBe(4m);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Time_windows_roll_forward_on_the_minute_timer_while_nothing_is_indexed()
    {
        var service = Service();
        await service.StartAsync(CancellationToken.None);
        service.Current.FiveHours.Tokens.Input.ShouldBe(1_000_000);
        var published = 0;
        _bus.Subscribe<TelemetryUpdated>(_ => published++);

        _time.Advance(TimeSpan.FromHours(6));

        service.Current.FiveHours.Tokens.Input.ShouldBe(0, "the burst six hours ago has left the 5-hour window");
        service.Current.RatePerMinute[^1].ShouldBe(0);
        published.ShouldBeGreaterThan(0);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Today_follows_a_time_zone_change_on_the_next_minute_tick()
    {
        var service = Service();
        await service.StartAsync(CancellationToken.None);
        service.Current.Today.Tokens.Input.ShouldBe(1_000_000);

        // 12:30 UTC is already 00:30 tomorrow at UTC+12, so the burst at 11:30 UTC belongs to yesterday there.
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("UTC+12", TimeSpan.FromHours(12), "UTC+12", "UTC+12"));
        _time.Advance(TelemetryService.RefreshInterval);

        service.Current.Today.Tokens.Input.ShouldBe(0);
        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>The fake clock, with a hook that runs while the service reads the time zone half-way through a recompute.</summary>
    private sealed class HookedTime(FakeTimeProvider inner) : TimeProvider
    {
        public Action? WhileReadingTheZone { get; set; }

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override TimeZoneInfo LocalTimeZone
        {
            get
            {
                var hook = WhileReadingTheZone;
                WhileReadingTheZone = null;
                hook?.Invoke();
                return inner.LocalTimeZone;
            }
        }

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            inner.CreateTimer(callback, state, dueTime, period);
    }

    [Fact]
    public async Task A_minute_tick_that_overlaps_a_transcript_update_does_not_leave_the_older_totals_behind()
    {
        var time = new HookedTime(_time);
        var service = new TelemetryService(_usage, _settings, _bus, time, NullLogger<TelemetryService>.Instance);
        await service.StartAsync(CancellationToken.None);
        var published = new List<TelemetrySnapshot>();
        _bus.Subscribe<TelemetryUpdated>(m =>
        {
            lock (published)
            {
                published.Add(m.Snapshot);
            }
        });
        var updateWentThrough = false;
        time.WhileReadingTheZone = () =>
        {
            // The indexer's thread brings usage while the tick has copied the buckets and is still adding them up;
            // the update lands and is published before the tick's older sums are done.
            var update = Task.Run(() => _bus.Publish(new TranscriptUpdated(new TranscriptUpdate
            {
                SessionId = "s1",
                TranscriptPath = "p",
                ObservedAt = _time.GetUtcNow(),
                Usage = [new UsageDelta("claude-sonnet-5", _time.GetUtcNow(), new TokenUsage(500, 0, 0, 0))],
            })));
            updateWentThrough = update.Wait(TimeSpan.FromSeconds(10));
        };

        _time.Advance(TelemetryService.RefreshInterval);

        updateWentThrough.ShouldBeTrue("the update must not wait for the tick's sums");
        service.Current.Today.Tokens.Input.ShouldBe(1_000_500);
        published[^1].Today.Tokens.Input.ShouldBe(1_000_500, "the snapshot published last is the newest");
        await service.StopAsync(CancellationToken.None);
    }
}
