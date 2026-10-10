using CodeSwitchX.UI.Infrastructure;

namespace CodeSwitchX.UI.Tests.Infrastructure;

public sealed class PendingTasksTests
{
    [Fact]
    public void Nothing_added_is_done()
    {
        new PendingTasks().All.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public void All_is_done_once_every_task_added_is()
    {
        var pending = new PendingTasks();
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        pending.Add(first.Task);
        pending.Add(second.Task);

        second.SetResult();
        pending.All.IsCompleted.ShouldBeFalse("the first still runs");
        first.SetResult();

        pending.All.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public void A_wait_taken_before_a_task_is_added_does_not_wait_for_it()
    {
        var pending = new PendingTasks();
        var first = new TaskCompletionSource();
        pending.Add(first.Task);
        var before = pending.All;

        pending.Add(new TaskCompletionSource().Task); // never ends
        first.SetResult();

        before.IsCompletedSuccessfully.ShouldBeTrue();
        pending.All.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task A_fault_stays_to_be_seen_by_what_waits_later()
    {
        var pending = new PendingTasks();
        pending.Add(Task.FromException(new InvalidOperationException("broke")));
        pending.Add(Task.CompletedTask);

        await Should.ThrowAsync<InvalidOperationException>(() => pending.All);
    }

    [Fact]
    public void After_a_fault_it_keeps_only_the_first_fault_not_every_task_since()
    {
        // Round 1 of #246: the chain grew with every task added after a fault.
        var pending = new PendingTasks();
        pending.Add(Task.FromException(new InvalidOperationException("broke")));
        pending.Add(Task.CompletedTask);
        pending.Add(Task.FromException(new InvalidOperationException("broke again")));
        pending.Add(Task.CompletedTask);

        pending.All.Exception!.InnerExceptions.ShouldHaveSingleItem().Message.ShouldBe("broke");
    }

    [Fact]
    public void A_cancelled_task_leaves_the_wait_cancelled()
    {
        var pending = new PendingTasks();
        pending.Add(Task.FromCanceled(new CancellationToken(canceled: true)));
        pending.Add(Task.CompletedTask);

        pending.All.IsCanceled.ShouldBeTrue();
    }

    [Fact]
    public async Task Done_waits_for_every_task_and_never_faults()
    {
        // Round 1 of #246: one faulted read ended every catch-up after it.
        var pending = new PendingTasks();
        var running = new TaskCompletionSource();
        pending.Add(Task.FromException(new InvalidOperationException("broke")));
        pending.Add(running.Task);

        var done = pending.Done;
        done.IsCompleted.ShouldBeFalse();
        running.SetResult();

        await done.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        done.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task Tasks_added_from_many_threads_at_once_are_all_waited_for()
    {
        var pending = new PendingTasks();
        var gates = Enumerable.Range(0, 64).Select(_ => new TaskCompletionSource()).ToList();

        await Task.WhenAll(gates.Select(g => Task.Run(() => pending.Add(g.Task))));
        foreach (var gate in gates.SkipLast(1))
        {
            gate.SetResult();
        }

        pending.All.IsCompleted.ShouldBeFalse("one is still running");
        gates[^1].SetResult();
        pending.All.IsCompletedSuccessfully.ShouldBeTrue();
    }
}
