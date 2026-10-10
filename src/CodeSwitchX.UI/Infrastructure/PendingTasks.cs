namespace CodeSwitchX.UI.Infrastructure;

/// <summary>
/// Work still running that something (a step that must come after it, or a test) waits for as one: <see cref="All"/>
/// completes once every task added is done. Once all have succeeded it starts afresh, so it does not grow for its owner's
/// life; a fault stays in it, to be seen by what waits. Safe to add to from any thread.
/// </summary>
internal sealed class PendingTasks
{
    private readonly object _lock = new();
    private Task _all = Task.CompletedTask;

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

    public void Add(Task task)
    {
        lock (_lock)
        {
            _all = _all.IsCompletedSuccessfully ? task : Task.WhenAll(_all, task);
        }
    }
}
