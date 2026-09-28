using CodeSwitchX.UI.Infrastructure;

namespace CodeSwitchX.UI.Tests.Infrastructure;

public class SingleInstanceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private readonly string _name = @"Local\csx-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task A_start_while_CodeSwitchX_runs_ends_and_brings_the_running_one_forward()
    {
        using var running = SingleInstance.TryClaim(_name);
        running.ShouldNotBeNull();
        var broughtForward = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        running.OnActivationRequested(() => broughtForward.TrySetResult());

        SingleInstance.TryClaim(_name).ShouldBeNull("the second start would fail to bind the Event API's pipe");

        await broughtForward.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_start_made_while_the_running_one_is_still_starting_brings_it_forward_once_it_listens()
    {
        using var running = SingleInstance.TryClaim(_name);
        running.ShouldNotBeNull();
        SingleInstance.TryClaim(_name).ShouldBeNull();

        var broughtForward = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        running.OnActivationRequested(() => broughtForward.TrySetResult());

        await broughtForward.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Once_the_running_one_has_ended_the_next_start_runs()
    {
        SingleInstance.TryClaim(_name)!.Dispose();

        using var next = SingleInstance.TryClaim(_name);

        next.ShouldNotBeNull();
    }
}
