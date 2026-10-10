namespace CodeSwitchX.UI.Infrastructure;

/// <summary>
/// Work still running that something (a step that must come after it, or a test) waits for as one: <see cref="All"/>
/// completes once every task added is done. Once all are done it starts afresh, so it does not grow for its owner's life;
/// the first fault stays in it, to be seen by what waits. Safe to add to from any thread.
/// </summary>
internal sealed class PendingTasks
{
    private readonly object _lock = new();
    private Task _all = Task.CompletedTask;

    /// <summary>The first task that faulted, kept once the rest are let go.</summary>
    private Task? _fault;

    /// <summary>Completes once every task added so far is done; faults if any of them did.</summary>
    public Task All
    {
        get
        {
            lock (_lock)
            {
                return _all;
            }
        }
    }

    /// <summary>Completes once every task added so far is done, faulted or not: for a step that must come after them all the same.</summary>
    public Task Done => All.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public void Add(Task task)
    {
        lock (_lock)
        {
            if (!_all.IsCompleted)
            {
                _all = Task.WhenAll(_all, task);
                return;
            }

            if (_fault is null && !_all.IsCompletedSuccessfully)
            {
                _fault = _all.IsFaulted ? FirstFault(_all) : _all;
            }

            _all = _fault is null ? task : Task.WhenAll(_fault, task);
        }
    }

    /// <summary>One task with the first exception of <paramref name="faulted"/>, not the chain it ended.</summary>
    private static Task FirstFault(Task faulted) => Task.FromException(faulted.Exception!.InnerException ?? faulted.Exception);
}
