using System.Runtime.InteropServices;
using CodeSwitchX.Ingest.Api;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>
/// Keeps CodeSwitchX to one instance per user. The first instance creates a named event; a later start finds it, sets
/// it so the running instance comes forward, and ends. A second instance could not bind the Event API's pipe
/// (it showed "failed to start") and would share the database and the log file with the first.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const int AsfwAny = -1;
    private readonly EventWaitHandle _signal;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(EventWaitHandle signal)
    {
        _signal = signal;
    }

    /// <summary>
    /// Per user across sessions, like the Event API's pipe and the database: the same account signed in twice (a second
    /// remote session) must not run a second instance either.
    /// </summary>
    public static string DefaultName => @"Global\" + EventApiOptions.DefaultPipeName();

    /// <summary>
    /// This process's claim when no other instance runs. Otherwise null, once the running instance has been asked to
    /// come forward. The event goes with the last handle to it, so an instance that crashed does not block the next start.
    /// </summary>
    public static SingleInstance? TryClaim(string name)
    {
        EventWaitHandle signal;
        bool createdNew;
        try
        {
            signal = new EventWaitHandle(false, EventResetMode.AutoReset, name, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            return null; // an instance runs that this process may not reach: one started as administrator
        }

        if (createdNew)
        {
            return new SingleInstance(signal);
        }

        // The user started this process, so it may hand the foreground on; the running instance could otherwise only
        // flash its taskbar button.
        AllowSetForegroundWindow(AsfwAny);
        signal.Set();
        signal.Dispose();
        return null;
    }

    /// <summary>
    /// Calls <paramref name="bringForward"/> on a pool thread for every later start, including one made before this
    /// was called: the event stays set until it is waited on.
    /// </summary>
    public void OnActivationRequested(Action bringForward) =>
        _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => bringForward(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _wait?.Unregister(null);
        _signal.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
