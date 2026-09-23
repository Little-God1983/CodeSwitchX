namespace CodeSwitchX.Core.Messaging;

/// <summary>In-process, synchronous publish/subscribe. Handlers run on the publisher's thread.</summary>
public interface IEventBus
{
    void Publish<T>(T message) where T : class;
    IDisposable Subscribe<T>(Action<T> handler) where T : class;
}
