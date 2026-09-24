using System.Windows.Threading;
using CodeSwitchX.UI.Infrastructure;

namespace CodeSwitchX.UI.Tests.Infrastructure;

public class WpfUiDispatcherTests
{
    [Fact]
    public async Task A_post_from_the_ui_thread_runs_after_posts_already_queued_from_other_threads()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = "test-ui" };
        thread.Start();
        var dispatcher = await ready.Task;
        var sut = new WpfUiDispatcher(dispatcher);
        var order = new List<string>();
        using var backgroundPosted = new ManualResetEventSlim();
        try
        {
            // The UI thread is held busy until this (non-UI) thread has queued "background" ...
            var uiWork = dispatcher.InvokeAsync(() =>
            {
                backgroundPosted.Wait(TestContext.Current.CancellationToken);
                // ... then the UI thread itself posts "ui". It must not overtake the queued item.
                sut.Post(() => order.Add("ui"));
            });
            sut.Post(() => order.Add("background"));
            backgroundPosted.Set();
            await uiWork;
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, TestContext.Current.CancellationToken);

            order.ShouldBe(["background", "ui"]);
        }
        finally
        {
            dispatcher.InvokeShutdown();
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task A_post_from_an_idle_ui_thread_runs_inline()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = "test-ui" };
        thread.Start();
        var dispatcher = await ready.Task;
        var sut = new WpfUiDispatcher(dispatcher);
        try
        {
            var ranInline = await dispatcher.InvokeAsync(() =>
            {
                var ran = false;
                sut.Post(() => ran = true);
                return ran;
            });

            ranInline.ShouldBeTrue("nothing is queued, so there is no order to preserve and no reason to defer");
        }
        finally
        {
            dispatcher.InvokeShutdown();
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
