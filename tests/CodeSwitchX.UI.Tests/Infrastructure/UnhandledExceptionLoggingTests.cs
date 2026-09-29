using System.Runtime.CompilerServices;
using CodeSwitchX.Tests;
using CodeSwitchX.UI.Infrastructure;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Tests.Infrastructure;

public class UnhandledExceptionLoggingTests
{
    [Fact]
    public async Task A_faulted_task_nobody_awaits_is_logged_when_it_is_collected()
    {
        // Only exceptions on the UI thread were logged: a fire-and-forget task that failed left no line anywhere.
        var log = new ListLogger<UnhandledExceptionLoggingTests>();
        using var hook = UnhandledExceptionLogging.Attach(log);

        DropFaultedTask();
        await Task.Yield();
        for (var i = 0; i < 5 && log.Entries.Count == 0; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        log.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Message.Contains("nobody awaited"));
    }

    /// <summary>In its own method, so nothing keeps the task alive once it returns.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropFaultedTask() => _ = Task.FromException(new InvalidOperationException("boom"));
}
