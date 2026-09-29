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
    /// <summary>Guards the buckets, the pricing and the order in which snapshots land; never held while a handler runs.</summary>
    private readonly Lock _gate = new();
    /// <summary>Guards who publishes; held only for the check, not while the bus calls the handlers.</summary>
    private readonly Lock _publishGate = new();
    private readonly SemaphoreSlim _reloads = new(1, 1);
    private readonly Dictionary<(string SessionId, string Model, DateTimeOffset Minute), UsageBucket> _buckets = [];
    private IDisposable? _subscription;
    private ITimer? _refreshTimer;
    private UsageAggregator _aggregator = new(PricingTable.Default);
    private long _tickets;
    private long _landed;
    private bool _publishing;
    private bool _publishAgain;
    private volatile TelemetrySnapshot _current;

    public TelemetryService(IUsageStore usage, ISettingsStore settings, IEventBus bus, TimeProvider time, ILogger<TelemetryService> logger)
    {
        _usage = usage;
        _settings = settings;
        _bus = bus;
        _time = time;
        _logger = logger;
        _current = TelemetrySnapshot.Empty(time.GetUtcNow());
    }

    public PricingTable Pricing { get; private set; } = PricingTable.Default;
    public TelemetrySnapshot Current => _current;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
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
        // One reload at a time, read and swap together: two overlapping reloads would otherwise swap in the order their
        // reads returned, and the older rules could land last.
        await _reloads.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The database holds only the user's own rules, each overriding the shipped default for its model.
            var rules = await _settings.GetPricingAsync(ct).ConfigureAwait(false);
            Work work;
            lock (_gate)
            {
                Pricing = new PricingTable(DefaultPricing.Rules.Concat(rules));
                _aggregator = new UsageAggregator(Pricing);
                work = TakeLocked();
            }

            Land(work, publish);
        }
        finally
        {
            _reloads.Release();
        }
    }

    private void OnTranscriptUpdated(TranscriptUpdated message)
    {
        if (message.Update.Usage.Count == 0)
        {
            return;
        }

        Work work;
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

                bucket.Add(delta.Tokens);
            }

            work = TakeLocked();
        }

        Land(work, publish: true);
    }

    private void Recompute(bool publish)
    {
        Work work;
        lock (_gate)
        {
            work = TakeLocked();
        }

        Land(work, publish);
    }

    /// <summary>A copy of the buckets with the ticket that orders it against the copies other recomputes took.</summary>
    private readonly record struct Work(long Ticket, UsageBucket[] Buckets, UsageAggregator Aggregator, DateTimeOffset Now);

    /// <summary>
    /// Under the lock, in the same critical section as the change that calls for the recompute: drops the buckets older
    /// than a week and hands out the copy to sum. The ticket comes with the copy, so a copy taken later has the higher one.
    /// </summary>
    private Work TakeLocked()
    {
        var now = _time.GetUtcNow();
        var cutoff = now - History;
        foreach (var stale in _buckets.Where(kv => kv.Key.Minute < cutoff).Select(kv => kv.Key).ToList())
        {
            _buckets.Remove(stale);
        }

        return new Work(++_tickets, _buckets.Values.ToArray(), _aggregator, now);
    }

    /// <summary>
    /// Sums the copy outside the lock (a week of buckets, three passes) and lands the snapshot unless a copy taken later
    /// has landed meanwhile: the minute timer and a transcript update can recompute at the same time, and the older copy
    /// must not become the current snapshot, or Today, the 5 h window and the sparkline would miss the newest usage until
    /// the next update or tick.
    /// </summary>
    private void Land(Work work, bool publish)
    {
        var snapshot = new TelemetrySnapshot(
            work.Aggregator.Today(work.Buckets, work.Now, _time.LocalTimeZone),
            work.Aggregator.Window(work.Buckets, work.Now, TimeSpan.FromHours(5)),
            work.Aggregator.RateSeries(work.Buckets, work.Now, RateMinutes),
            work.Now);
        lock (_gate)
        {
            if (work.Ticket < _landed)
            {
                return;
            }

            _landed = work.Ticket;
            _current = snapshot;
        }

        if (publish)
        {
            PublishCurrent();
        }
    }

    /// <summary>
    /// One thread publishes at a time, outside every lock, so a handler of the snapshot (the bar's UI marshal) holds no
    /// transcript update back and can take the telemetry lock itself. A snapshot that lands while a publish runs is not
    /// waited for: the publishing thread sends the current snapshot once more before it leaves, so the newest comes last.
    /// </summary>
    private void PublishCurrent()
    {
        lock (_publishGate)
        {
            if (_publishing)
            {
                _publishAgain = true;
                return;
            }

            _publishing = true;
        }

        while (true)
        {
            _bus.Publish(new TelemetryUpdated(_current));
            lock (_publishGate)
            {
                if (!_publishAgain)
                {
                    _publishing = false;
                    return;
                }

                _publishAgain = false;
            }
        }
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _subscription?.Dispose();
        _reloads.Dispose();
    }
}
