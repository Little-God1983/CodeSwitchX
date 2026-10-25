using CodeSwitchX.UI.Raven;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Raven;

public sealed class TrafficWatcherTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero));

    private static readonly TrafficWatcher.Floor Free = new(false, false, 0, 0, false, false, false, false, false);

    /// <summary>#152: the pause runs from the last thing Raven said or played; nothing said yet, none is left.</summary>
    [Fact]
    public void The_pause_runs_from_the_last_announcement()
    {
        var watcher = new TrafficWatcher(_time);
        watcher.PauseLeft.ShouldBe(TimeSpan.Zero);

        watcher.Announced();
        _time.Advance(TimeSpan.FromSeconds(1));

        watcher.PauseLeft.ShouldBe(TimeSpan.FromSeconds(2));
        _time.Advance(TimeSpan.FromSeconds(5));
        watcher.PauseLeft.ShouldBe(TimeSpan.Zero);
    }

    /// <summary>#152: the pause set again to what it is says nothing: what waits for it keeps its wait.</summary>
    [Fact]
    public void Only_a_new_pause_is_a_change()
    {
        var watcher = new TrafficWatcher(_time);
        var changes = 0;
        watcher.PauseChanged += (_, _) => changes++;

        watcher.Pause = TrafficWatcher.DefaultPause;
        watcher.Pause = TimeSpan.FromSeconds(10);

        changes.ShouldBe(1);
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
        TrafficWatcher.IsFree(Free with { Due = true }).ShouldBeFalse();
        TrafficWatcher.IsFree(Free with { Sounding = true }).ShouldBeFalse("a chat's sound plays (#306)");
    }
}
