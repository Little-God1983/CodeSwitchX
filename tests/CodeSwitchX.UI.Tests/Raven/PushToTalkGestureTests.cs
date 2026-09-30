namespace CodeSwitchX.UI.Tests.Raven;

using CodeSwitchX.UI.Raven;
using Microsoft.Extensions.Time.Testing;

public sealed class PushToTalkGestureTests
{
    private readonly FakeTimeProvider _time = new();
    private PushToTalkGesture NewGesture() => new(_time);

    [Fact]
    public void A_hold_starts_on_press_and_stops_on_release()
    {
        var g = NewGesture();
        g.Press().ShouldBe(PushToTalkAction.Start);
        _time.Advance(TimeSpan.FromMilliseconds(800));
        g.Release().ShouldBe(PushToTalkAction.Stop);
    }

    // The hotkey's press is handled when the UI thread gets to it; timed from the message, a held key the UI saw late is
    // still a hold, and its release stops rather than latches.
    [Fact]
    public void A_press_handled_late_is_timed_from_when_the_key_went_down()
    {
        var g = NewGesture();
        g.Press(TimeSpan.FromSeconds(1)).ShouldBe(PushToTalkAction.Start);

        g.Release().ShouldBe(PushToTalkAction.Stop);
    }

    [Fact]
    public void A_tap_handled_late_by_less_than_the_threshold_still_latches()
    {
        var g = NewGesture();
        g.Press(TimeSpan.FromMilliseconds(100)).ShouldBe(PushToTalkAction.Start);
        _time.Advance(TimeSpan.FromMilliseconds(100));

        g.Release().ShouldBe(PushToTalkAction.None);
    }

    [Fact]
    public void A_quick_tap_latches_and_the_next_press_stops()
    {
        var g = NewGesture();
        g.Press().ShouldBe(PushToTalkAction.Start);
        _time.Advance(TimeSpan.FromMilliseconds(120));
        g.Release().ShouldBe(PushToTalkAction.None);
        _time.Advance(TimeSpan.FromSeconds(5));
        g.Press().ShouldBe(PushToTalkAction.Stop);
        g.Release().ShouldBe(PushToTalkAction.None);
    }

    [Fact]
    public void Autorepeat_presses_while_held_do_nothing()
    {
        var g = NewGesture();
        g.Press().ShouldBe(PushToTalkAction.Start);
        g.Press().ShouldBe(PushToTalkAction.None);
        g.Press().ShouldBe(PushToTalkAction.None);
        _time.Advance(TimeSpan.FromSeconds(1));
        g.Release().ShouldBe(PushToTalkAction.Stop);
    }

    [Fact]
    public void Exactly_the_threshold_counts_as_a_hold()
    {
        var g = NewGesture();
        g.Press();
        _time.Advance(PushToTalkGesture.HoldThreshold);
        g.Release().ShouldBe(PushToTalkAction.Stop);
    }

    [Fact]
    public void A_release_without_a_press_does_nothing()
    {
        NewGesture().Release().ShouldBe(PushToTalkAction.None);
    }

    [Fact]
    public void Reset_forgets_a_latch()
    {
        var g = NewGesture();
        g.Press();
        g.Release();
        g.Reset();
        g.Press().ShouldBe(PushToTalkAction.Start);
    }
}
