namespace CodeSwitchX.UI.Infrastructure;

public static class UiDispatcherExtensions
{
    /// <summary>
    /// Runs <paramref name="read"/> on the UI thread, which alone touches the view models' collections, and hands its
    /// result back off it. Waits at most <paramref name="timeout"/>: a UI thread that never answers (hung, or its dispatcher
    /// shut down) fails the read with a <see cref="TimeoutException"/> instead of holding the caller for good. An exception
    /// from <paramref name="read"/> comes back to the caller.
    /// </summary>
    public static async Task<T> InvokeAsync<T>(this IUiDispatcher ui, Func<T> read, TimeSpan timeout, CancellationToken ct = default)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(() =>
        {
            try
            {
                result.TrySetResult(read());
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
        });
        return await result.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
    }
}
