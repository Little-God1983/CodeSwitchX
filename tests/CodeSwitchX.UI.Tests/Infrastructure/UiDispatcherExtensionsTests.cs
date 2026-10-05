using CodeSwitchX.UI.Infrastructure;

namespace CodeSwitchX.UI.Tests.Infrastructure;

public sealed class UiDispatcherExtensionsTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task A_read_the_caller_gave_up_on_before_it_began_is_never_done()
    {
        // The UI thread is busy past the wait: the caller is told it failed, so it must not happen once the thread frees up.
        var ui = new HeldDispatcher();
        var done = false;

        await Should.ThrowAsync<TimeoutException>(() => ui.InvokeAsync(() => done = true, Short, TestContext.Current.CancellationToken));
        ui.RunHeld();

        done.ShouldBeFalse();
    }

    [Fact]
    public async Task A_read_cancelled_before_it_began_is_never_done()
    {
        var ui = new HeldDispatcher();
        var done = false;

        await Should.ThrowAsync<OperationCanceledException>(() => ui.InvokeAsync(() => done = true, TimeSpan.FromMinutes(1), new CancellationToken(canceled: true)));
        ui.RunHeld();

        done.ShouldBeFalse();
    }

    [Fact]
    public async Task A_read_that_began_before_the_wait_ran_out_is_waited_for()
    {
        using var began = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        // The read has begun by the time the wait starts: a thread pool slow to start it cannot let the wait run out first
        // (#144: the read was then withdrawn, never began, and the test waited for good).
        var ui = new BeginningDispatcher(began);
        var read = ui.InvokeAsync(() =>
        {
            began.Set();
            go.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            return 7;
        }, Short, TestContext.Current.CancellationToken);

        await Task.Delay(Short * 5, TestContext.Current.CancellationToken);
        read.IsCompleted.ShouldBeFalse();
        go.Set();

        (await read.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldBe(7);
    }

    /// <summary>A UI thread of its own for each post, which returns from the post only once the work has begun.</summary>
    private sealed class BeginningDispatcher(ManualResetEventSlim began) : IUiDispatcher
    {
        public void Post<T>(Action<T> action, T state) => Post(() => action(state));

        public void Post(Action action)
        {
            new Thread(() => action()) { IsBackground = true }.Start();
            began.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue("the posted work began");
        }
    }

    /// <summary>A UI thread that is busy: what is posted waits until the test runs it.</summary>
    private sealed class HeldDispatcher : IUiDispatcher
    {
        private readonly List<Action> _held = [];

        public void Post<T>(Action<T> action, T state) => Post(() => action(state));

        public void Post(Action action)
        {
            lock (_held)
            {
                _held.Add(action);
            }
        }

        public void RunHeld()
        {
            List<Action> held;
            lock (_held)
            {
                held = [.. _held];
                _held.Clear();
            }

            held.ForEach(a => a());
        }
    }
}
