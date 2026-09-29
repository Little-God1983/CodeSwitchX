using System.Collections.Concurrent;

namespace CodeSwitchX.UI.Tests;

/// <summary>
/// The UI thread as a queue the test pumps by hand: a continuation posted here (the rest of an async view-model method
/// after an await) runs only when the test says so, so a click can be slipped in before it, as on the real dispatcher.
/// </summary>
internal sealed class QueuedSynchronizationContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

    public int Pending => _queue.Count;

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state) => d(state);

    /// <summary>Waits for one continuation to be posted, then runs it.</summary>
    public void RunOne(TimeSpan patience)
    {
        if (!_queue.TryTake(out var item, patience))
        {
            throw new TimeoutException("no continuation was posted");
        }

        item.Callback(item.State);
    }

    public void RunUntil(Func<bool> done, TimeSpan patience)
    {
        var deadline = DateTime.UtcNow + patience;
        while (!done() && DateTime.UtcNow < deadline)
        {
            if (_queue.TryTake(out var item, TimeSpan.FromMilliseconds(20)))
            {
                item.Callback(item.State);
            }
        }
    }
}
