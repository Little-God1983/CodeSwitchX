using System.Windows.Threading;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>
/// Marshals bus callbacks onto the WPF dispatcher in the order they were posted. A post made on the UI thread runs
/// inline only while nothing is queued; otherwise it would overtake earlier posts from other threads and, for
/// example, apply an older session snapshot after a newer one.
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;
    private int _queued;

    public WpfUiDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Post(Action action) => Post(static a => a(), action);

    /// <summary>
    /// Inline, nothing is allocated. Queued, WPF's own work item (its DispatcherOperation) and one small holder of the
    /// action and its state are: the callback the dispatcher runs is cached.
    /// </summary>
    public void Post<T>(Action<T> action, T state)
    {
        if (_dispatcher.CheckAccess() && Volatile.Read(ref _queued) == 0)
        {
            action(state);
            return;
        }

        Interlocked.Increment(ref _queued);
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, RunQueued, new Queued<T>(this, action, state));
    }

    private static readonly SendOrPostCallback RunQueued = static queued => ((IQueued)queued!).Run();

    private interface IQueued
    {
        void Run();
    }

    private sealed class Queued<T>(WpfUiDispatcher owner, Action<T> action, T state) : IQueued
    {
        public void Run()
        {
            try
            {
                action(state);
            }
            finally
            {
                Interlocked.Decrement(ref owner._queued);
            }
        }
    }
}
