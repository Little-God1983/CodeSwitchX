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

    // DI builds the recorder on the UI thread (an STA with a context); the panel starts it from the thread pool. An
    // enumerator made with the recorder would live in the STA, and the pool's calls on it would wait for that thread.
    [Fact(Explicit = true)]
    public async Task A_recorder_built_on_a_blocked_sta_thread_starts_from_the_thread_pool()
    {
        string? deviceId;
        using (var catalog = new WasapiMicrophoneCatalog())
        {
            deviceId = (catalog.Default() ?? catalog.List()[0]).Id;
        }

        WasapiMicrophoneRecorder? recorder = null;
        using var built = new ManualResetEventSlim();
        var release = 0;
        Exception? startFailure = null;
        var startedInTime = false;
        var sta = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NeverPumpedContext());
            recorder = new WasapiMicrophoneRecorder();
            built.Set();
            // Blocked without pumping, like a busy UI thread: Thread.Sleep pumps no COM calls, a wait would.
            while (Volatile.Read(ref release) == 0)
            {
                Thread.Sleep(10);
            }

            recorder.Dispose();
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        built.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ShouldBeTrue();

        try
        {
            var start = Task.Run(() => recorder!.Start(deviceId), TestContext.Current.CancellationToken);
            try
            {
                await start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                startedInTime = true;
            }
            catch (TimeoutException)
            {
            }
            catch (Exception ex)
            {
                startedInTime = true;
                startFailure = ex;
            }

            if (startedInTime && startFailure is null)
            {
                await Task.Delay(200, TestContext.Current.CancellationToken);
                await Task.Run(recorder!.Stop, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            Volatile.Write(ref release, 1);
            sta.Join(TimeSpan.FromSeconds(10));
        }

        startFailure.ShouldBeNull();
        startedInTime.ShouldBeTrue("the start must not wait for the blocked STA thread");
    }

    private sealed class NeverPumpedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }
}
