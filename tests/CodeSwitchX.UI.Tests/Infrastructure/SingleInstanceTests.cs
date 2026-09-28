using CodeSwitchX.UI.Infrastructure;

namespace CodeSwitchX.UI.Tests.Infrastructure;

public class SingleInstanceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private readonly string _name = "csx-test-" + Guid.NewGuid().ToString("N");

    /// <summary>Both "instances" live in this test process.</summary>
    private static IReadOnlyList<int> Here() => [Environment.ProcessId];

    [Fact]
    public void A_start_while_CodeSwitchX_runs_brings_the_running_one_forward_and_ends()
    {
        using var running = Claim(Patience).Instance;
        running.ShouldNotBeNull();
        var broughtForward = false;
        running.OnActivationRequested(() => broughtForward = true);

        Claim(Patience).Result.ShouldBe(ClaimResult.BroughtForward, "a second instance would fail to bind the Event API's pipe");

        broughtForward.ShouldBeTrue();
    }

    [Fact]
    public void A_start_made_while_the_running_one_is_still_starting_is_answered_once_it_listens()
    {
        using var running = Claim(Patience).Instance;
        running.ShouldNotBeNull();
        _ = Task.Delay(200, TestContext.Current.CancellationToken).ContinueWith(_ => running.OnActivationRequested(() => true), TaskScheduler.Default);

        Claim(Patience).Result.ShouldBe(ClaimResult.BroughtForward);
    }

    [Fact]
    public void A_running_one_whose_window_does_not_come_forward_is_reported()
    {
        using var running = Claim(Patience).Instance;
        running.ShouldNotBeNull();
        running.OnActivationRequested(() => false); // a UI thread that did not get to it in time

        Claim(TimeSpan.FromMilliseconds(300)).Result.ShouldBe(ClaimResult.NoAnswer, "ending without a word would leave the user with nothing on screen");
    }

    [Fact]
    public async Task An_answer_that_came_too_late_for_one_start_does_not_answer_the_next()
    {
        using var running = Claim(Patience).Instance;
        running.ShouldNotBeNull();
        var slow = true;
        running.OnActivationRequested(() =>
        {
            if (!slow)
            {
                return false;
            }

            Thread.Sleep(600);
            return true;
        });
        Claim(TimeSpan.FromMilliseconds(200)).Result.ShouldBe(ClaimResult.NoAnswer);
        await Task.Delay(800, TestContext.Current.CancellationToken); // the late answer arrives
        slow = false;

        Claim(TimeSpan.FromMilliseconds(300)).Result.ShouldBe(ClaimResult.NoAnswer, "the window did not come forward for this start");
    }

    [Fact]
    public async Task A_running_one_in_another_session_is_reported_and_left_alone()
    {
        using var running = Claim(Patience).Instance;
        running.ShouldNotBeNull();

        SingleInstance.Claim(_name, Patience, runningInThisSession: () => []).Result.ShouldBe(ClaimResult.InAnotherSession);

        var asked = false;
        running.OnActivationRequested(() => asked = true);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        asked.ShouldBeFalse("its window would come forward where the user cannot see it");
    }

    [Theory]
    [InlineData("event")] // with the default security of a plain event, as another account would create it
    [InlineData("mutex")]
    public void A_name_held_by_something_other_than_this_users_CodeSwitchX_is_reported_and_does_not_stop_the_start(string kind)
    {
        using IDisposable squatter = kind == "event"
            ? new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\" + _name)
            : new Mutex(false, @"Global\" + _name);

        var claim = Claim(Patience);

        claim.Result.ShouldBe(ClaimResult.Unavailable);
        claim.Problem.ShouldNotBeNull();
    }

    [Fact]
    public void Once_the_running_one_has_ended_the_next_start_runs()
    {
        Claim(Patience).Instance!.Dispose();

        using var next = Claim(Patience).Instance;

        next.ShouldNotBeNull();
    }

    private InstanceClaim Claim(TimeSpan answerTimeout) => SingleInstance.Claim(_name, answerTimeout, Here);
}
