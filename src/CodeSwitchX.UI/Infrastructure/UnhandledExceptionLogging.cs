using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>
/// Logs what fails unseen off the UI thread. WPF's DispatcherUnhandledException covers the UI thread alone: an exception
/// that ends the process from another thread, or a task that faulted and nobody awaited (a fire-and-forget load), left
/// no line in the log.
/// </summary>
public static class UnhandledExceptionLogging
{
    public static IDisposable Attach(ILogger logger)
    {
        UnhandledExceptionEventHandler onUnhandled = (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception off the UI thread; the process ends");
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, e) =>
        {
            logger.LogError(e.Exception, "A task faulted and nobody awaited it");
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += onUnhandled;
        TaskScheduler.UnobservedTaskException += onUnobserved;
        return new Detach(() =>
        {
            AppDomain.CurrentDomain.UnhandledException -= onUnhandled;
            TaskScheduler.UnobservedTaskException -= onUnobserved;
        });
    }

    private sealed class Detach(Action detach) : IDisposable
    {
        public void Dispose() => detach();
    }
}
