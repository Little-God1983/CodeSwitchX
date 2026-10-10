namespace CodeSwitchX.UI.Infrastructure;

/// <summary>
/// Work still running that something (a step that must come after it, or a test) waits for as one: <see cref="All"/>
/// completes once every task added so far is done. Only tasks still running are held, so a task that never ends keeps no
/// others alive; the first fault stays, to be seen by what waits. Safe to add to from any thread.
/// </summary>
internal sealed class PendingTasks
{
    private readonly object _lock = new();
    private readonly HashSet<Task> _running = [];

    /// <summary>The first task added that faulted or was cancelled.</summary>
    private Task? _fault;

    /// <summary>Completes once every task added so far is done; faults (or is cancelled) if any of them, or any before, did.</summary>
    public Task All
    {
        get
        {
            lock (_lock)
            {
                if (_running.Count == 0)
                {
                    return _fault ?? Task.CompletedTask;
                }

                var running = Task.WhenAll(_running.ToArray());
                return _fault is null ? running : Task.WhenAll(_fault, running);
            }
        }
    }

    /// <summary>Completes once every task added so far is done, faulted or not: for a step that must come after them all the same.</summary>
    public Task Done => All.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public void Add(Task task)
    {
        lock (_lock)
        {
            _running.Add(task);
        }

        task.ContinueWith(Finished, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void Finished(Task task)
    {
        lock (_lock)
        {
            _running.Remove(task);
            if (!task.IsCompletedSuccessfully)
            {
                _fault ??= task;
            }
        }
    }
}
