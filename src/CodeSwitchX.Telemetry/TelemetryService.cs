using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Telemetry;

/// <summary>Keeps the last seven days of usage buckets in memory and publishes a fresh snapshot after every transcript update.</summary>
public sealed class TelemetryService : IHostedService, IDisposable, IPricingProvider
{
    public static readonly TimeSpan History = TimeSpan.FromDays(7);
    public const int RateMinutes = 60;

    /// <summary>Windows ("Today", 5 hours, the rate sparkline) roll forward on this cadence even when nothing is indexed.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    private readonly IUsageStore _usage;
    private readonly ISettingsStore _settings;
    private readonly IEventBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<TelemetryService> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string SessionId, string Model, DateTimeOffset Minute), UsageBucket> _buckets = [];
    private IDisposable? _subscription;
    private ITimer? _refreshTimer;
    private UsageAggregator _aggregator = new(PricingTable.Default);

    public TelemetryService(IUsageStore usage, ISettingsStore settings, IEventBus bus, TimeProvider time, ILogger<TelemetryService> logger)
    {
        _usage = usage;
        _settings = settings;
        _bus = bus;
        _time = time;
        _logger = logger;
        Current = TelemetrySnapshot.Empty(time.GetUtcNow());
    }

    public PricingTable Pricing { get; private set; } = PricingTable.Default;
    public TelemetrySnapshot Current { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _settings.EnsurePricingDefaultsAsync(DefaultPricing.Rules.ToArray(), cancellationToken).ConfigureAwait(false);
        await ReloadPricingAsync(cancellationToken, publish: false).ConfigureAwait(false);

        var now = _time.GetUtcNow();
        var stored = await _usage.GetBucketsAsync(now - History, now.AddMinutes(1), cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var bucket in stored)
            {
                _buckets[(bucket.SessionId, bucket.Model, bucket.MinuteUtc)] = bucket;
            }
        }

        _subscription = _bus.Subscribe<TranscriptUpdated>(OnTranscriptUpdated);
        Recompute(publish: true);
        _refreshTimer = _time.CreateTimer(_ => Recompute(publish: true), null, RefreshInterval, RefreshInterval);
        _logger.LogInformation("Telemetry loaded {Count} usage buckets", stored.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    public Task ReloadPricingAsync(CancellationToken ct) => ReloadPricingAsync(ct, publish: true);

    private async Task ReloadPricingAsync(CancellationToken ct, bool publish)
    {
        var rules = await _settings.GetPricingAsync(ct).ConfigureAwait(false);
        Pricing = new PricingTable(DefaultPricing.Rules.Concat(rules));
        _aggregator = new UsageAggregator(Pricing);
        Recompute(publish);
    }

    private void OnTranscriptUpdated(TranscriptUpdated message)
    {
        if (message.Update.Usage.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var delta in message.Update.Usage)
            {
                var minute = UsageBucket.FloorToMinute(delta.At);
                var key = (message.Update.SessionId, delta.Model, minute);
                if (!_buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new UsageBucket { SessionId = message.Update.SessionId, Model = delta.Model, MinuteUtc = minute };
                    _buckets[key] = bucket;
                }

                bucket.Input += delta.Tokens.Input;
                bucket.Output += delta.Tokens.Output;
                bucket.CacheWrite += delta.Tokens.CacheWrite;
                bucket.CacheRead += delta.Tokens.CacheRead;
            }
        }

        Recompute(publish: true);
    }

    private void Recompute(bool publish)
    {
        var now = _time.GetUtcNow();
        UsageBucket[] buckets;
        lock (_gate)
        {
            var cutoff = now - History;
            foreach (var stale in _buckets.Where(kv => kv.Key.Minute < cutoff).Select(kv => kv.Key).ToList())
            {
                _buckets.Remove(stale);
            }

            buckets = _buckets.Values.ToArray();
        }

        Current = new TelemetrySnapshot(
            _aggregator.Today(buckets, now, _time.LocalTimeZone),
            _aggregator.Window(buckets, now, TimeSpan.FromHours(5)),
            _aggregator.RateSeries(buckets, now, RateMinutes),
            now);

        if (publish)
        {
            _bus.Publish(new TelemetryUpdated(Current));
        }
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _subscription?.Dispose();
    }
}
