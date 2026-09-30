namespace CodeSwitchX.Voice.Tests.Audio;

using System.Diagnostics;
using CodeSwitchX.Voice.Audio;

public sealed class WasapiRecorderThreadingTests
{
    // Needs a real capture device, so it is explicit: run with --explicit on.
    [Fact(Explicit = true)]
    public void Stop_does_not_wait_on_the_synchronization_context_of_the_thread_that_started_it()
    {
        long stopMs = 0;
        var thread = new Thread(() =>
        {
            // Like a UI thread: a context that only runs posted work when the thread pumps, which it never does here.
            SynchronizationContext.SetSynchronizationContext(new NeverPumpedContext());
            using var catalog = new WasapiMicrophoneCatalog();
            var device = catalog.Default() ?? catalog.List()[0];
            var recorder = new WasapiMicrophoneRecorder();
            recorder.Start(device.Id);
            Thread.Sleep(300);
            var watch = Stopwatch.StartNew();
            recorder.Stop();
            stopMs = watch.ElapsedMilliseconds;
        });
        thread.Start();
        thread.Join();

        stopMs.ShouldBeLessThan(1000);
    }

    private sealed class NeverPumpedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }
}
