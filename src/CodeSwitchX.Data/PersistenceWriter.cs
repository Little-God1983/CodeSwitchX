using System.Threading.Channels;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Data;

/// <summary>
/// Drains bus messages into SQLite in batches so bursts of tool events never block the publisher
/// or contend on the database. Usage deltas and the transcript cursors that cover them are committed in one
/// transaction; a batch whose write fails is kept and retried on the next flush instead of being dropped.
/// </summary>
public sealed class PersistenceWriter : BackgroundService
{
    /// <summary>Upper bound on retained records after repeated failures; beyond it the retained batch is dropped with an error.</summary>
    internal const int MaxCarriedItems = 5000;

    /// <summary>How long the shutdown flush may take to drain the queue before the rest is given up.</summary>
    internal static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2);

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
    private Batch? _carry;
    private DateTimeOffset? _lastPrune;
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
            if (m.Update.Usage.Count > 0 || m.Update.Cursor is not null)
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
            // shutting down: StopAsync drains what is still queued
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // The drain lives here, not at the end of ExecuteAsync. Since .NET 10, BackgroundService starts ExecuteAsync
        // through Task.Run with the stopping token, so a stop that beats the thread pool cancels the loop before it runs.
        if (ExecuteTask is { IsCompleted: false })
        {
            // The host gave up waiting while a flush is still running; draining now would race it for the queue.
            _logger.LogWarning("The shutdown flush was skipped because the last flush is still running; {Count} records were not saved", Pending + (_carry?.Count ?? 0));
            return;
        }

        // Bounded by the host's stop budget as well as the grace period. Pending, not the channel's Count: a
        // single-reader channel does not support counting.
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        grace.CancelAfter(ShutdownGrace);
        try
        {
            do
            {
                await FlushAsync(grace.Token).ConfigureAwait(false);
            }
            while (Pending > 0);
        }
        catch (OperationCanceledException)
        {
            // out of time; what is left is reported below
        }

        // A failed flush keeps its batch for the next flush, but at shutdown there is none.
        var unsaved = Pending + (_carry?.Count ?? 0);
        if (unsaved > 0)
        {
            _logger.LogWarning("The shutdown flush ended with {Count} records not saved", unsaved);
        }
    }

    internal async Task FlushAsync(CancellationToken ct)
    {
        await PruneIfDueAsync(ct).ConfigureAwait(false);
        var batch = _carry ?? new Batch();
        _carry = null;
        var taken = 0;

        while (taken < _options.MaxBatch && _queue.Reader.TryRead(out var item))
        {
            taken++;
            batch.Add(item, _options.StorePayloads);
        }

        if (batch.IsEmpty)
        {
            return;
        }

        try
        {
            // Each step clears what it saved, so a retry after a failure only repeats the part that did not commit
            // (events are append-only and must not be written twice).
            await _sessions.UpsertAsync(batch.Sessions.Values.ToArray(), ct).ConfigureAwait(false);
            batch.Sessions.Clear();
            await _sessions.AppendEventsAsync(batch.Events.ToArray(), ct).ConfigureAwait(false);
            batch.Events.Clear();
            await _usage.CommitAsync(batch.Buckets.Values.ToArray(), batch.Cursors.Values.ToArray(), batch.MessageIds.ToArray(), ct).ConfigureAwait(false);
            batch.Buckets.Clear();
            batch.Cursors.Clear();
            batch.MessageIds.Clear();
        }
        catch (OperationCanceledException)
        {
            // A cancelled tick (shutdown, mostly) must not lose what was already taken from the queue: the shutdown flush retries it.
            _carry = batch;
            throw;
        }
        catch (Exception ex)
        {
            if (batch.Count > MaxCarriedItems)
            {
                _logger.LogError(ex, "Persisting {Count} retained items keeps failing; dropping them", batch.Count);
            }
            else
            {
                _carry = batch;
                _logger.LogWarning(ex, "Persisting a batch of {Count} items failed; it will be retried on the next flush", batch.Count);
            }
        }
        finally
        {
            Interlocked.Add(ref _pending, -taken);
        }
    }

    /// <summary>Hook events are append-only and only serve the recent-activity view; without pruning the database grows without bound.</summary>
    private async Task PruneIfDueAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (_lastPrune is { } last && now - last < _options.PruneInterval)
        {
            return;
        }

        _lastPrune = now;
        try
        {
            var removed = await _sessions.PruneEventsAsync(now - _options.EventRetention, ct).ConfigureAwait(false);
            if (removed > 0)
            {
                _logger.LogInformation("Pruned {Count} hook events older than {Retention}", removed, _options.EventRetention);
            }

            var forgotten = await _usage.PruneSeenMessagesAsync(_options.SeenMessageIdsKept, ct).ConfigureAwait(false);
            if (forgotten > 0)
            {
                _logger.LogInformation("Pruned {Count} remembered message ids beyond the newest {Kept}", forgotten, _options.SeenMessageIdsKept);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Pruning hook events failed; retrying in {Interval}", _options.PruneInterval);
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

    /// <summary>
    /// Records collapsed for one write: latest snapshot per session, every hook event, usage per minute bucket,
    /// latest cursor per file, and the message ids the usage came from.
    /// </summary>
    private sealed class Batch
    {
        public Dictionary<string, SessionRecord> Sessions { get; } = new(StringComparer.Ordinal);
        public List<SessionEventRecord> Events { get; } = [];
        public Dictionary<(string SessionId, string Model, DateTimeOffset Minute), UsageBucket> Buckets { get; } = [];
        public Dictionary<string, TranscriptCursor> Cursors { get; } = new(StringComparer.Ordinal);
        public HashSet<string> MessageIds { get; } = new(StringComparer.Ordinal);

        public int Count => Sessions.Count + Events.Count + Buckets.Count + Cursors.Count + MessageIds.Count;
        public bool IsEmpty => Count == 0;

        public void Add(object item, bool storePayloads)
        {
            switch (item)
            {
                case SessionSnapshot snapshot:
                    Sessions[snapshot.SessionId] = SessionRecord.FromSnapshot(snapshot);
                    break;
                case HookEvent hook:
                    Events.Add(new SessionEventRecord
                    {
                        SessionId = hook.SessionId,
                        Kind = hook.EventName,
                        ToolName = hook.ToolName,
                        At = hook.At,
                        PayloadJson = storePayloads ? hook.RawJson : null,
                    });
                    break;
                case TranscriptUpdate update:
                    foreach (var delta in update.Usage)
                    {
                        var minute = UsageBucket.FloorToMinute(delta.At);
                        var key = (update.SessionId, delta.Model, minute);
                        if (!Buckets.TryGetValue(key, out var bucket))
                        {
                            bucket = new UsageBucket { SessionId = update.SessionId, Model = delta.Model, MinuteUtc = minute };
                            Buckets[key] = bucket;
                        }

                        bucket.Input += delta.Tokens.Input;
                        bucket.Output += delta.Tokens.Output;
                        bucket.CacheWrite += delta.Tokens.CacheWrite;
                        bucket.CacheRead += delta.Tokens.CacheRead;
                    }

                    if (update.Cursor is { } cursor)
                    {
                        Cursors[cursor.Path] = cursor;
                    }

                    MessageIds.UnionWith(update.MessageIds);
                    break;
            }
        }
    }
}
