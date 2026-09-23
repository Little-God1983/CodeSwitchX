using System.Threading.Channels;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Data;

/// <summary>
/// Drains bus messages into SQLite in batches so bursts of tool events never block the publisher
/// or contend on the database.
/// </summary>
public sealed class PersistenceWriter : BackgroundService
{
    private readonly IEventBus _bus;
    private readonly ISessionStore _sessions;
    private readonly IUsageStore _usage;
    private readonly TimeProvider _time;
    private readonly ILogger<PersistenceWriter> _logger;
    private readonly PersistenceWriterOptions _options;
    private readonly Channel<object> _queue = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });

    // The flush timer is created in StartAsync (not ExecuteAsync) so it exists as soon as the service is started;
    // BackgroundService may defer ExecuteAsync, and a timer created there would miss ticks that happen in between.
    private readonly Channel<bool> _flushSignal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly List<IDisposable> _subscriptions = [];
    private ITimer? _flushTimer;
    private int _pending;

    public PersistenceWriter(IEventBus bus, ISessionStore sessions, IUsageStore usage, TimeProvider time,
        ILogger<PersistenceWriter> logger, PersistenceWriterOptions options)
    {
        _bus = bus;
        _sessions = sessions;
        _usage = usage;
        _time = time;
        _logger = logger;
        _options = options;
    }

    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Attach to the bus. Called from <see cref="StartAsync"/>; tests call it directly.</summary>
    public void Subscribe()
    {
        if (_subscriptions.Count > 0)
        {
            return;
        }

        _subscriptions.Add(_bus.Subscribe<SessionChanged>(m => Enqueue(m.Current)));
        _subscriptions.Add(_bus.Subscribe<HookEventReceived>(m => Enqueue(m.Event)));
        _subscriptions.Add(_bus.Subscribe<TranscriptUpdated>(m =>
        {
            if (m.Update.Usage.Count > 0)
            {
                Enqueue(m.Update);
            }
        }));
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Subscribe();
        _flushTimer ??= _time.CreateTimer(_ => _flushSignal.Writer.TryWrite(true), null, _options.FlushInterval, _options.FlushInterval);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _flushSignal.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                _flushSignal.Reader.TryRead(out _);
                await FlushAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down: flush what is left with a short grace period
        }

        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await FlushAsync(grace.Token).ConfigureAwait(false);
    }

    internal async Task FlushAsync(CancellationToken ct)
    {
        var sessions = new Dictionary<string, SessionRecord>(StringComparer.Ordinal);
        var events = new List<SessionEventRecord>();
        var buckets = new Dictionary<(string SessionId, string Model, DateTimeOffset Minute), UsageBucket>();
        var taken = 0;

        while (taken < _options.MaxBatch && _queue.Reader.TryRead(out var item))
        {
            taken++;
            switch (item)
            {
                case SessionSnapshot snapshot:
                    sessions[snapshot.SessionId] = SessionRecord.FromSnapshot(snapshot);
                    break;
                case HookEvent hook:
                    events.Add(new SessionEventRecord
                    {
                        SessionId = hook.SessionId,
                        Kind = hook.EventName,
                        ToolName = hook.ToolName,
                        At = hook.At,
                        PayloadJson = _options.StorePayloads ? hook.RawJson : null,
                    });
                    break;
                case TranscriptUpdate update:
                    foreach (var delta in update.Usage)
                    {
                        var minute = UsageBucket.FloorToMinute(delta.At);
                        var key = (update.SessionId, delta.Model, minute);
                        if (!buckets.TryGetValue(key, out var bucket))
                        {
                            bucket = new UsageBucket { SessionId = update.SessionId, Model = delta.Model, MinuteUtc = minute };
                            buckets[key] = bucket;
                        }

                        bucket.Input += delta.Tokens.Input;
                        bucket.Output += delta.Tokens.Output;
                        bucket.CacheWrite += delta.Tokens.CacheWrite;
                        bucket.CacheRead += delta.Tokens.CacheRead;
                    }

                    break;
            }
        }

        if (taken == 0)
        {
            return;
        }

        try
        {
            await _sessions.UpsertAsync(sessions.Values.ToArray(), ct).ConfigureAwait(false);
            await _sessions.AppendEventsAsync(events, ct).ConfigureAwait(false);
            await _usage.AddUsageAsync(buckets.Values.ToArray(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Persisting a batch of {Count} items failed; the batch is dropped", taken);
        }
        finally
        {
            Interlocked.Add(ref _pending, -taken);
        }
    }

    public override void Dispose()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        base.Dispose();
    }

    private void Enqueue(object item)
    {
        if (_queue.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref _pending);
        }
    }
}
