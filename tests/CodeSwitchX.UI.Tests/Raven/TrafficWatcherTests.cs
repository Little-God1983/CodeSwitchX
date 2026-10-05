using CodeSwitchX.UI.Raven;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Raven;

public sealed class TrafficWatcherTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero));

    private static readonly TrafficWatcher.Floor Free = new(false, false, 0, 0, false, false, false, false);

    [Fact]
    public void The_first_sound_is_allowed_and_one_inside_the_cooldown_is_not()
    {
        var watcher = new TrafficWatcher(_time);

        watcher.TrySound(floorFree: true).ShouldBeTrue();
        _time.Advance(TimeSpan.FromSeconds(9));
        watcher.TrySound(floorFree: true).ShouldBeFalse("inside the 10 s cooldown");
        _time.Advance(TimeSpan.FromSeconds(1));
        watcher.TrySound(floorFree: true).ShouldBeTrue("the cooldown is over");
    }

    [Fact]
    public void A_refused_sound_does_not_start_the_cooldown_again()
    {
        var watcher = new TrafficWatcher(_time);
        watcher.TrySound(floorFree: true);

        _time.Advance(TimeSpan.FromSeconds(5));
        watcher.TrySound(floorFree: true).ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(5));

        watcher.TrySound(floorFree: true).ShouldBeTrue("nothing was saved up, and the silent one did not count");
    }

    [Fact]
    public void An_announcement_starts_the_cooldown()
    {
        var watcher = new TrafficWatcher(_time) { Cooldown = TimeSpan.FromSeconds(20) };

        watcher.Announced();
        _time.Advance(TimeSpan.FromSeconds(15));

        watcher.TrySound(floorFree: true).ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(5));
        watcher.TrySound(floorFree: true).ShouldBeTrue();
    }

    [Fact]
    public void A_busy_floor_means_silent()
    {
        var watcher = new TrafficWatcher(_time);

        watcher.TrySound(floorFree: false).ShouldBeFalse();
        watcher.CooledDown.ShouldBeTrue("a sound not made starts no cooldown");
    }

    [Fact]
    public void With_the_sound_off_no_chat_makes_one()
    {
        var watcher = new TrafficWatcher(_time) { SoundOn = false };

        watcher.TrySound(floorFree: true).ShouldBeFalse();
    }

    [Fact]
    public void The_chat_s_own_news_waits_for_the_cooldown_only_when_asked_to()
    {
        var watcher = new TrafficWatcher(_time);
        watcher.Announced();
        _time.Advance(TimeSpan.FromSeconds(5));

        watcher.MaySpeakOwnNews.ShouldBeTrue("off by default");
        watcher.OwnNewsWaits = true;
        watcher.MaySpeakOwnNews.ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(5));
        watcher.MaySpeakOwnNews.ShouldBeTrue();
    }

    [Fact]
    public void The_floor_is_free_only_when_nobody_talks_and_no_yes_is_awaited()
    {
        TrafficWatcher.IsFree(Free).ShouldBeTrue();
        TrafficWatcher.IsFree(Free with { Capturing = true }).ShouldBeFalse();
        TrafficWatcher.IsFree(Free with { Holding = true }).ShouldBeFalse();
        TrafficWatcher.IsFree(Free with { Pending = 1 }).ShouldBeFalse();
        TrafficWatcher.IsFree(Free with { Asking = 1 }).ShouldBeFalse();
        TrafficWatcher.IsFree(Free with { Telling = true }).ShouldBeFalse();
        TrafficWatcher.IsFree(Free with { Speaking = true }).ShouldBeFalse();
        TrafficWatcher.IsFree(Free with { OpenSpeech = true }).ShouldBeFalse();
        TrafficWatcher.IsFree(Free with { AwaitingYes = true }).ShouldBeFalse();
    }
}
