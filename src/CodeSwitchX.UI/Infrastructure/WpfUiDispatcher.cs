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

    public void Post(Action action)
    {
        if (_dispatcher.CheckAccess() && Volatile.Read(ref _queued) == 0)
        {
            action();
            return;
        }

        Interlocked.Increment(ref _queued);
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            try
            {
                action();
            }
            finally
            {
                Interlocked.Decrement(ref _queued);
            }
        });
    }
}
