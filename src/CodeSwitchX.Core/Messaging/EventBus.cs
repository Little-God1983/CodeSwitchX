using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Core.Messaging;

public sealed class EventBus : IEventBus
{
    private readonly ILogger<EventBus> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<Type, List<Subscription>> _subscriptions = [];

    public EventBus(ILogger<EventBus> logger)
    {
        _logger = logger;
    }

    public void Publish<T>(T message) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        Subscription[] targets;
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(typeof(T), out var list) || list.Count == 0)
            {
                return;
            }

            targets = list.ToArray();
        }

        foreach (var target in targets)
        {
            try
            {
                ((Action<T>)target.Handler)(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Event handler for {MessageType} threw", typeof(T).Name);
            }
        }
    }

    public IDisposable Subscribe<T>(Action<T> handler) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new Subscription(this, typeof(T), handler);
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(typeof(T), out var list))
            {
                list = [];
                _subscriptions[typeof(T)] = list;
            }

            list.Add(subscription);
        }

        return subscription;
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(subscription.MessageType, out var list))
            {
                list.Remove(subscription);
            }
        }
    }

    private sealed class Subscription(EventBus owner, Type messageType, Delegate handler) : IDisposable
    {
        public Type MessageType { get; } = messageType;
        public Delegate Handler { get; } = handler;

        public void Dispose() => owner.Remove(this);
    }
}
