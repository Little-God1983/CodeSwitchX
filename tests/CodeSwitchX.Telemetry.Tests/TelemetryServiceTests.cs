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
    public async Task Start_seeds_pricing_defaults_and_loads_recent_buckets()
    {
        var service = Service();

        await service.StartAsync(CancellationToken.None);

        await _settings.Received(1).EnsurePricingDefaultsAsync(Arg.Is<IReadOnlyCollection<PricingRule>>(r => r.Count == DefaultPricing.Rules.Count), Arg.Any<CancellationToken>());
        await _usage.Received(1).GetBucketsAsync(_time.GetUtcNow().AddDays(-7), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        service.Current.Today.Tokens.Input.ShouldBe(1_000_000);
        service.Current.Today.Cost.ShouldBe(2m);
        service.Current.FiveHours.Tokens.Input.ShouldBe(1_000_000);
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
    }
}
