using System.Runtime.CompilerServices;

namespace CodeSwitchX.UI.Infrastructure;

public static class UiDispatcherExtensions
{
    private const int Waiting = 0, Running = 1, Withdrawn = 2;

    /// <summary>
    /// Runs <paramref name="read"/> on the UI thread, which alone touches the view models' collections, and hands its
    /// result back off it. Waits at most <paramref name="timeout"/>: a UI thread that never answers (hung, or its dispatcher
    /// shut down) fails the read with a <see cref="TimeoutException"/> instead of holding the caller for good. A read that
    /// has not begun by then, or by <paramref name="ct"/>, is withdrawn, so a caller told it failed can say it was not done;
    /// one that has begun is waited for. An exception from <paramref name="read"/> comes back to the caller.
    /// </summary>
    public static async Task<T> InvokeAsync<T>(this IUiDispatcher ui, Func<T> read, TimeSpan timeout, CancellationToken ct = default)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new StrongBox<int>(Waiting);
        ui.Post(() =>
        {
            if (Interlocked.CompareExchange(ref state.Value, Running, Waiting) != Waiting)
            {
                return;
            }

            try
            {
                result.TrySetResult(read());
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
        });

        try
        {
            return await result.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            if (Interlocked.CompareExchange(ref state.Value, Withdrawn, Waiting) == Waiting)
            {
                throw;
            }

            // It began on the UI thread just as the wait ran out: what it does is the answer.
            return await result.Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread, and completes once it has run, or once <paramref name="timeout"/>
    /// on <paramref name="time"/> has passed with the UI thread still busy. Unlike <see cref="InvokeAsync{T}"/>, nothing is
    /// withdrawn: work that must happen still runs when the thread gets to it, and only the wait gives up, so none hangs on
    /// a dispatcher that shut down with the app. An exception from <paramref name="action"/> is the UI thread's, as any
    /// post's.
    /// </summary>
    public static async Task RunAsync(this IUiDispatcher ui, Action action, TimeSpan timeout, TimeProvider time)
    {
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = ran.Task.WaitAsync(timeout, time); // armed before the post: the timeout runs from it
        ui.Post(() =>
        {
            try
            {
                action();
            }
            finally
            {
                ran.TrySetResult();
            }
        });

        try
        {
            await wait.ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Left to run when it can.
        }
    }
}
