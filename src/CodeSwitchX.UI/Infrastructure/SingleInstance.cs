using System.Runtime.InteropServices;
using CodeSwitchX.Core;
using CodeSwitchX.Ingest.Api;

namespace CodeSwitchX.UI.Infrastructure;

public enum ClaimResult
{
    /// <summary>No other instance runs: this process holds the claim.</summary>
    Claimed,

    /// <summary>The running instance brought its window forward.</summary>
    BroughtForward,

    /// <summary>The running instance did not answer in time: it is still starting, closing, or hung.</summary>
    NoAnswer,

    /// <summary>The running instance is in another Windows session of this user, whose window cannot be seen from here.</summary>
    InAnotherSession,

    /// <summary>The name is held by something that is not this user's CodeSwitchX, so nothing guards this start.</summary>
    Unavailable,
}

public sealed record InstanceClaim(ClaimResult Result, SingleInstance? Instance = null, Exception? Problem = null);

/// <summary>
/// Keeps CodeSwitchX to one instance per user. The first instance creates a named event; a later start sets it, waits
/// for the running instance to answer that its window came forward, and ends. A second instance could not bind the
/// Event API's pipe (it showed "failed to start") and would share the database and the log file with the first.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>How long a later start waits for the running window to come forward.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(5);

    // Per user across sessions (the Global namespace), like the Event API's pipe and the database. CurrentUserOnly
    // refuses an object this user did not create with these options, so another account cannot take the name first
    // and keep CodeSwitchX from starting.
    private static readonly NamedWaitHandleOptions Options = new() { CurrentUserOnly = true, CurrentSessionOnly = false };

    private readonly EventWaitHandle _request;
    private readonly EventWaitHandle _answer;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(EventWaitHandle request, EventWaitHandle answer)
    {
        _request = request;
        _answer = answer;
    }

    /// <summary>The options choose the namespace, so the name has no Global\ prefix.</summary>
    public static string DefaultName => EventApiOptions.DefaultPipeName();

    /// <param name="name">The claim's name.</param>
    /// <param name="answerTimeout">How long to wait for a running instance to bring its window forward.</param>
    /// <param name="runningInThisSession">The other CodeSwitchX processes in this session; tests pass their own.</param>
    public static InstanceClaim Claim(string name, TimeSpan answerTimeout, Func<IReadOnlyList<int>>? runningInThisSession = null)
    {
        EventWaitHandle? request = null;
        EventWaitHandle answer;
        bool createdNew;
        try
        {
            request = new EventWaitHandle(false, EventResetMode.AutoReset, name, Options, out createdNew);
            answer = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-answer", Options);
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or System.IO.IOException)
        {
            request?.Dispose();
            return new InstanceClaim(ClaimResult.Unavailable, Problem: ex);
        }

        if (createdNew)
        {
            return new InstanceClaim(ClaimResult.Claimed, new SingleInstance(request, answer));
        }

        using (request)
        using (answer)
        {
            var running = (runningInThisSession ?? RunningInThisSession)();
            if (running.Count == 0)
            {
                return new InstanceClaim(ClaimResult.InAnotherSession);
            }

            // The user started this process, so it may hand the foreground on: to the running instance alone.
            foreach (var pid in running)
            {
                AllowSetForegroundWindow(pid);
            }

            answer.Reset(); // an answer that came too late for an earlier start
            request.Set();
            return new InstanceClaim(answer.WaitOne(answerTimeout) ? ClaimResult.BroughtForward : ClaimResult.NoAnswer);
        }
    }

    /// <summary>
    /// Runs <paramref name="bringForward"/> on a pool thread for every later start, including one made before this was
    /// called: the request stays set until it is waited on. The start is answered when it returns true.
    /// </summary>
    public void OnActivationRequested(Func<bool> bringForward) =>
        _wait = ThreadPool.RegisterWaitForSingleObject(_request, (_, _) => Answer(bringForward), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _wait?.Unregister(null);
        _request.Dispose();
        _answer.Dispose();
    }

    private void Answer(Func<bool> bringForward)
    {
        try
        {
            if (bringForward())
            {
                _answer.Set();
            }
        }
        catch (Exception)
        {
            // Not answered, so the later start says the window did not come forward. An exception here, on a pool
            // thread, would end the process.
        }
    }

    private static IReadOnlyList<int> RunningInThisSession() => ProcessNamesakes.InThisSession();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
