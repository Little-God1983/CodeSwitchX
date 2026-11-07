using CodeSwitchX.Core.Usage;
using CodeSwitchX.UI.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.UI.Tests.Telemetry;

public sealed class PlanLimitsViewModelTests : IDisposable
{
    /// <summary>A Wednesday.</summary>
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly FakeReader _reader = new();
    private readonly PlanLimitsViewModel _vm;

    public PlanLimitsViewModelTests()
    {
        _vm = new PlanLimitsViewModel(_reader, new ImmediateDispatcher(), _time, NullLogger<PlanLimitsViewModel>.Instance);
    }

    public void Dispose() => _vm.Dispose();

    private static PlanReading Reading(int fiveHour = 54, TimeSpan? inFive = null, int week = 38, DateTimeOffset? weekAt = null) => new(PlanReadingKind.Read, new PlanLimits(
        new PlanLimit(fiveHour, Start + (inFive ?? new TimeSpan(2, 41, 0))),
        new PlanLimit(week, weekAt ?? new DateTimeOffset(2026, 11, 9, 6, 0, 0, TimeSpan.Zero))));

    private async Task StartedAsync(PlanReading reading)
    {
        _reader.Next = reading;
        _vm.Start();
        await _vm.PendingRead;
    }

    /// <summary>Time passes as on the bar, tick by tick, and any reading a tick began ends.</summary>
    private async Task PassAsync(TimeSpan span)
    {
        var end = _time.GetUtcNow() + span;
        while (_time.GetUtcNow() < end)
        {
            var left = end - _time.GetUtcNow();
            _time.Advance(left < PlanLimitsViewModel.TickInterval ? left : PlanLimitsViewModel.TickInterval);
            await _vm.PendingRead;
        }
    }

    [Fact]
    public async Task The_bar_shows_percent_and_countdown_for_five_hours_and_percent_and_day_for_the_week()
    {
        await StartedAsync(Reading());

        _vm.FiveHourText.ShouldBe("5h 54% · resets in 2h 41m");
        _vm.WeekText.ShouldBe("week 38% · resets Mon 06:00");
    }

    [Fact]
    public async Task Each_limit_s_bar_has_its_percent_and_short_text()
    {
        await StartedAsync(Reading());

        _vm.FiveHourBar!.Percent.ShouldBe(54);
        _vm.FiveHourBar.Text.ShouldBe("54% · 2h 41m");
        _vm.WeekBar!.Percent.ShouldBe(38);
        _vm.WeekBar.Text.ShouldBe("38% · Mon 06:00");
    }

    [Fact]
    public async Task The_pace_mark_is_how_much_of_the_window_has_passed()
    {
        // 2h 41m of 5 hours left: 2h 19m passed. Wed 12:00 to Mon 06:00 is 4 days 18 hours of 7 days left: 54 hours passed.
        await StartedAsync(Reading());

        _vm.FiveHourBar!.Pace!.Value.ShouldBe(139 / 300.0 * 100, 0.001);
        _vm.WeekBar!.Pace!.Value.ShouldBe(54 / 168.0 * 100, 0.001);
    }

    [Fact]
    public async Task The_pace_mark_moves_as_time_passes()
    {
        await StartedAsync(Reading());

        await PassAsync(TimeSpan.FromMinutes(1));

        _vm.FiveHourBar!.Pace!.Value.ShouldBe(140 / 300.0 * 100, 0.001);
        _vm.FiveHourBar.Text.ShouldBe("54% · 2h 40m");
    }

    [Theory]
    [InlineData(57, true)] // 46.3% of the window passed: more than ten points past it
    [InlineData(56, false)] // past the mark, but within the margin
    [InlineData(40, false)]
    public async Task A_limit_spent_clearly_faster_than_its_window_passes_is_ahead(int percent, bool ahead)
    {
        await StartedAsync(Reading(fiveHour: percent));

        _vm.FiveHourBar!.IsAhead.ShouldBe(ahead);
    }

    [Fact]
    public async Task A_reset_further_off_than_the_window_puts_the_mark_at_the_start()
    {
        await StartedAsync(Reading(inFive: TimeSpan.FromHours(6)));

        _vm.FiveHourBar!.Pace.ShouldBe(0);
    }

    [Fact]
    public async Task A_limit_with_no_reset_has_no_pace_mark_and_is_never_ahead()
    {
        await StartedAsync(new PlanReading(PlanReadingKind.Read, new PlanLimits(new PlanLimit(54, null), new PlanLimit(38, null))));

        _vm.FiveHourBar!.Pace.ShouldBeNull();
        _vm.FiveHourBar.IsAhead.ShouldBeFalse();
        _vm.FiveHourBar.Text.ShouldBe("54%");
        _vm.WeekBar!.Text.ShouldBe("38%");
    }

    [Fact]
    public async Task With_the_reset_passed_the_bar_is_empty_until_it_is_read_again()
    {
        await StartedAsync(Reading(inFive: TimeSpan.FromMinutes(2)));
        _reader.Hold = new TaskCompletionSource();

        _time.Advance(TimeSpan.FromMinutes(1));
        _time.Advance(TimeSpan.FromMinutes(1));

        _vm.FiveHourBar.ShouldBe(new LimitBarState(0, null, "reading again…"), "the old percent tells of the window that ended");
        _reader.Next = Reading(fiveHour: 0, inFive: TimeSpan.FromHours(5));
        _reader.Hold.SetResult();
        await _vm.PendingRead;
    }

    [Fact]
    public async Task Without_figures_there_are_no_bars()
    {
        await StartedAsync(new PlanReading(PlanReadingKind.Read, new PlanLimits(null, new PlanLimit(38, Start.AddDays(5)))));

        _vm.FiveHourBar.ShouldBeNull();
        _vm.WeekBar.ShouldNotBeNull();

        _reader.Next = new PlanReading(PlanReadingKind.NoFigures);
        await PassAsync(PlanLimitsViewModel.ReadInterval);

        _vm.WeekBar.ShouldBeNull();
    }

    [Fact]
    public async Task The_day_and_time_of_the_week_are_the_user_s_own()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"));

        await StartedAsync(Reading(weekAt: new DateTimeOffset(2026, 11, 9, 20, 0, 0, TimeSpan.Zero)));

        _vm.WeekText.ShouldBe("week 38% · resets Tue 05:00");
    }

    [Theory]
    [InlineData(5, 59, "Mon 06:00")] // the same reset read "5:59am" and "6am" within the hour: it must not flip on the bar
    [InlineData(6, 0, "Mon 06:00")]
    [InlineData(6, 2, "Mon 06:00")]
    [InlineData(22, 39, "Mon 22:40")]
    [InlineData(23, 58, "Tue 00:00")]
    public async Task The_week_s_time_is_shown_to_the_nearest_five_minutes(int hour, int minute, string shown)
    {
        await StartedAsync(Reading(weekAt: new DateTimeOffset(2026, 11, 9, hour, minute, 0, TimeSpan.Zero)));

        _vm.WeekText.ShouldBe($"week 38% · resets {shown}");
    }

    [Fact]
    public async Task The_detail_says_when_the_figures_were_read()
    {
        await StartedAsync(Reading());

        _vm.Detail.ShouldBe("The real limits of your plan, as Claude Code's /usage says them. Read at 12:00.");
    }

    [Fact]
    public async Task Figures_from_another_day_say_which_day()
    {
        await StartedAsync(Reading());
        _reader.Next = new PlanReading(PlanReadingKind.Failed);

        await PassAsync(TimeSpan.FromHours(25));

        _vm.Detail.ShouldBe("The real limits of your plan, as Claude Code's /usage says them. Read Wed 12:00.");
    }

    [Fact]
    public async Task The_countdown_ticks_without_a_new_reading()
    {
        await StartedAsync(Reading());

        await PassAsync(TimeSpan.FromMinutes(2));

        _vm.FiveHourText.ShouldBe("5h 54% · resets in 2h 39m");
        _reader.Reads.ShouldBe(1);
    }

    [Theory]
    [InlineData(45 * 60, "45m")]
    [InlineData(2 * 3600, "2h 0m")]
    [InlineData(30, "1m")] // the minute it is in: never "0m" while it still counts
    [InlineData(61, "2m")]
    public async Task The_countdown_counts_whole_minutes_up(int seconds, string shown)
    {
        await StartedAsync(Reading(inFive: TimeSpan.FromSeconds(seconds)));

        _vm.FiveHourText.ShouldBe($"5h 54% · resets in {shown}");
    }

    [Fact]
    public async Task A_new_reading_comes_after_five_minutes_and_not_before()
    {
        await StartedAsync(Reading());

        await PassAsync(PlanLimitsViewModel.ReadInterval - PlanLimitsViewModel.TickInterval);
        _reader.Reads.ShouldBe(1);

        _reader.Next = Reading(fiveHour: 60);
        await PassAsync(PlanLimitsViewModel.TickInterval);

        _reader.Reads.ShouldBe(2);
        _vm.FiveHourText!.ShouldStartWith("5h 60%");
    }

    [Fact]
    public async Task A_reading_comes_right_after_a_reset_passes()
    {
        await StartedAsync(Reading(inFive: TimeSpan.FromMinutes(2)));
        _reader.Next = Reading(fiveHour: 0, inFive: TimeSpan.FromHours(5));

        await PassAsync(TimeSpan.FromMinutes(2));

        _reader.Reads.ShouldBe(2, "at the reset, not at the five minutes");
        _vm.FiveHourText!.ShouldStartWith("5h 0%");
    }

    [Fact]
    public async Task With_the_reset_passed_and_no_new_reading_the_bar_says_it_is_read_again()
    {
        await StartedAsync(Reading(inFive: TimeSpan.FromMinutes(2)));
        _reader.Hold = new TaskCompletionSource();

        _time.Advance(TimeSpan.FromMinutes(1));
        _time.Advance(TimeSpan.FromMinutes(1));

        _vm.FiveHourText.ShouldBe("5h reading again…");
        _vm.WeekText.ShouldBe("week 38% · resets Mon 06:00", "its own reset has not passed");
        _reader.Next = Reading(fiveHour: 0, inFive: TimeSpan.FromHours(5));
        _reader.Hold.SetResult();
        await _vm.PendingRead;
    }

    [Fact]
    public async Task A_reading_still_on_the_old_window_is_tried_again_soon()
    {
        // The text's reset can be a minute before the real one, so a reading at that time still tells of the old window.
        await StartedAsync(Reading(inFive: TimeSpan.FromMinutes(2)));
        await PassAsync(TimeSpan.FromMinutes(2));

        _reader.Reads.ShouldBe(2);
        _vm.FiveHourText.ShouldBe("5h reading again…");

        _reader.Next = Reading(fiveHour: 0, inFive: TimeSpan.FromHours(5));
        await PassAsync(PlanLimitsViewModel.RetryAfterReset);

        _reader.Reads.ShouldBe(3);
        _vm.FiveHourText!.ShouldStartWith("5h 0%");
    }

    [Fact]
    public async Task A_reset_that_stays_past_is_not_asked_about_every_minute_for_ever()
    {
        await StartedAsync(Reading(inFive: TimeSpan.FromMinutes(1)));

        await PassAsync(TimeSpan.FromHours(1));

        var retries = (int)(PlanLimitsViewModel.EndedAfter / PlanLimitsViewModel.RetryAfterReset);
        var reads = (int)(TimeSpan.FromHours(1) / PlanLimitsViewModel.ReadInterval);
        _reader.Reads.ShouldBeInRange(2 + retries, 2 + retries + reads, "a minute apart for a while after the reset, then every five minutes");
        _vm.FiveHourText.ShouldBe("5h 0%", "by then the window counts as ended");
    }

    [Fact]
    public async Task A_window_that_ended_with_the_account_idle_stands_at_zero_once_the_retries_run_out()
    {
        // #376: if /usage keeps telling the old reset while nothing is used, the window ended and no new one began.
        await StartedAsync(Reading(inFive: TimeSpan.FromMinutes(1)));
        await PassAsync(TimeSpan.FromMinutes(1));
        _vm.FiveHourText.ShouldBe("5h reading again…", "a reading right after the reset may still tell of the old window");

        await PassAsync(PlanLimitsViewModel.EndedAfter);

        _reader.Reads.ShouldBe(2 + (int)(PlanLimitsViewModel.EndedAfter / PlanLimitsViewModel.RetryAfterReset), "the start, the reset, and a minute apart until five minutes after it");
        _vm.FiveHourText.ShouldBe("5h 0%");
        _vm.FiveHourBar.ShouldBe(new LimitBarState(0, null, "0%"));
        _vm.WeekText.ShouldBe("week 38% · resets Mon 06:00", "its own window goes on");

        await PassAsync(TimeSpan.FromHours(3));
        _vm.FiveHourText.ShouldBe("5h 0%", "it stays so, and does not go back to being read again");
    }

    [Fact]
    public async Task The_detail_says_which_window_ended_and_that_its_zero_is_inferred()
    {
        await StartedAsync(Reading(inFive: TimeSpan.FromHours(-3)));

        _vm.Detail.ShouldNotBeNull().ShouldContain("The 5-hour window ended with nothing used since");
        _vm.Detail.ShouldNotContain("The week");

        _reader.Next = Reading();
        await PassAsync(PlanLimitsViewModel.ReadInterval);
        _vm.Detail.ShouldNotBeNull().ShouldNotContain("ended", Case.Insensitive);
    }

    [Fact]
    public async Task A_reading_just_inside_the_retries_still_says_it_is_read_again()
    {
        await StartedAsync(Reading(inFive: -(PlanLimitsViewModel.EndedAfter - TimeSpan.FromSeconds(1))));

        _vm.FiveHourText.ShouldBe("5h reading again…");
        _vm.FiveHourBar.ShouldBe(new LimitBarState(0, null, "reading again…"));
    }

    [Fact]
    public async Task A_first_reading_long_past_its_reset_stands_at_zero_at_once()
    {
        await StartedAsync(Reading(inFive: TimeSpan.FromHours(-3), weekAt: Start.AddDays(-1)));

        _vm.FiveHourText.ShouldBe("5h 0%");
        _vm.WeekText.ShouldBe("week 0%");
        _vm.WeekBar.ShouldBe(new LimitBarState(0, null, "0%"));
    }

    [Fact]
    public async Task Two_resets_close_together_each_get_their_minutes_of_retries()
    {
        // Both stay past: the week's, two minutes after the 5-hour one, is not cut short by the retries the first one had.
        await StartedAsync(Reading(inFive: TimeSpan.FromMinutes(1), weekAt: Start.AddMinutes(3)));

        await PassAsync(TimeSpan.FromMinutes(1) + PlanLimitsViewModel.EndedAfter);
        _vm.FiveHourText.ShouldBe("5h 0%");
        _vm.WeekText.ShouldBe("week reading again…");

        await PassAsync(TimeSpan.FromMinutes(2));
        var minutes = (int)((TimeSpan.FromMinutes(3) + PlanLimitsViewModel.EndedAfter) / PlanLimitsViewModel.RetryAfterReset);
        _reader.Reads.ShouldBe(1 + minutes, "a minute apart from the first reset until the second counts as ended");
        _vm.WeekText.ShouldBe("week 0%");
    }

    [Fact]
    public async Task A_later_reset_gets_its_own_retries_after_an_ended_window()
    {
        await StartedAsync(Reading(inFive: TimeSpan.FromMinutes(1), weekAt: Start.AddMinutes(20)));
        await PassAsync(TimeSpan.FromMinutes(20));
        var reads = _reader.Reads;

        await PassAsync(TimeSpan.FromMinutes(3));

        _reader.Reads.ShouldBe(reads + 3);
    }

    [Fact]
    public async Task A_failed_reading_after_an_ended_window_keeps_it_as_any_figure()
    {
        await StartedAsync(Reading(inFive: TimeSpan.FromHours(-3)));
        _reader.Next = new PlanReading(PlanReadingKind.Failed);

        await PassAsync(PlanLimitsViewModel.ReadInterval);

        _reader.Reads.ShouldBe(2);
        _vm.FiveHourText.ShouldBe("5h 0%", "a failed reading leaves the last figures standing, with the time they were read");
    }

    [Fact]
    public async Task Use_after_an_ended_window_shows_the_new_one_at_the_next_reading()
    {
        await StartedAsync(Reading(inFive: TimeSpan.FromHours(-3)));
        _vm.FiveHourText.ShouldBe("5h 0%");
        await PassAsync(PlanLimitsViewModel.ReadInterval - TimeSpan.FromSeconds(15));
        _reader.Reads.ShouldBe(1, "an ended window is not looked at again every minute");
        _reader.Next = Reading(fiveHour: 4, inFive: TimeSpan.FromHours(5) + PlanLimitsViewModel.ReadInterval - TimeSpan.FromMinutes(1));

        await PassAsync(TimeSpan.FromSeconds(15));

        _reader.Reads.ShouldBe(2);
        _vm.FiveHourText.ShouldBe("5h 4% · resets in 4h 59m");
    }

    [Fact]
    public async Task A_stuck_reset_does_not_make_the_other_limit_s_reset_wait()
    {
        // The week's reset passes and stays in the figures, as readings fail; its retries run out, and the 5-hour reset is still read on time.
        var stale = Start.AddMinutes(1);
        await StartedAsync(Reading(inFive: TimeSpan.FromMinutes(18), weekAt: stale));
        _reader.Next = new PlanReading(PlanReadingKind.Failed);
        await PassAsync(TimeSpan.FromMinutes(17));
        _vm.WeekText.ShouldBe("week reading again…", "a failed reading keeps the last figures, past reset and all");
        var reads = _reader.Reads;
        _reader.Next = Reading(fiveHour: 0, inFive: TimeSpan.FromHours(5), weekAt: stale);

        await PassAsync(TimeSpan.FromMinutes(1));

        _reader.Reads.ShouldBe(reads + 1, "read at the 5-hour reset, not at the next five minutes");
        _vm.FiveHourText!.ShouldStartWith("5h 0%", Case.Sensitive);
    }

    [Fact]
    public async Task A_failed_reading_keeps_the_last_figures()
    {
        await StartedAsync(Reading());
        _reader.Next = new PlanReading(PlanReadingKind.Failed);

        await PassAsync(PlanLimitsViewModel.ReadInterval);

        _reader.Reads.ShouldBe(2);
        _vm.FiveHourText!.ShouldStartWith("5h 54%");
        _vm.WeekText!.ShouldStartWith("week 38%");
    }

    [Fact]
    public async Task A_failed_first_reading_shows_nothing_and_tries_again_later()
    {
        await StartedAsync(new PlanReading(PlanReadingKind.Failed));

        _vm.FiveHourText.ShouldBeNull();
        _vm.WeekText.ShouldBeNull();

        _reader.Next = Reading();
        await PassAsync(PlanLimitsViewModel.ReadInterval);

        _vm.FiveHourText!.ShouldStartWith("5h 54%");
    }

    [Fact]
    public async Task A_reader_that_throws_counts_as_a_failed_reading()
    {
        await StartedAsync(Reading());
        _reader.Throws = new InvalidOperationException("no");

        await PassAsync(PlanLimitsViewModel.ReadInterval);

        _vm.FiveHourText!.ShouldStartWith("5h 54%");
        _reader.Throws = null;
        _reader.Next = Reading(fiveHour: 60);
        await PassAsync(PlanLimitsViewModel.ReadInterval);
        _vm.FiveHourText!.ShouldStartWith("5h 60%"); // a throw does not stop the readings
    }

    [Fact]
    public async Task No_figures_any_more_hides_the_old_ones()
    {
        await StartedAsync(Reading());
        _reader.Next = new PlanReading(PlanReadingKind.NoFigures);

        await PassAsync(PlanLimitsViewModel.ReadInterval);

        _vm.FiveHourText.ShouldBeNull();
        _vm.WeekText.ShouldBeNull();
    }

    [Fact]
    public async Task A_limit_that_cannot_be_read_hides_only_itself()
    {
        await StartedAsync(new PlanReading(PlanReadingKind.Read, new PlanLimits(null, new PlanLimit(38, Start.AddDays(5)))));

        _vm.FiveHourText.ShouldBeNull();
        _vm.WeekText!.ShouldStartWith("week 38%");
    }

    [Fact]
    public async Task A_limit_with_no_reset_shows_its_percent_alone()
    {
        await StartedAsync(new PlanReading(PlanReadingKind.Read, new PlanLimits(new PlanLimit(0, null), new PlanLimit(38, null))));

        _vm.FiveHourText.ShouldBe("5h 0%");
        _vm.WeekText.ShouldBe("week 38%");
    }

    [Fact]
    public async Task A_reading_that_spent_a_turn_ends_the_readings()
    {
        await StartedAsync(Reading());
        _reader.Next = new PlanReading(PlanReadingKind.SpentATurn);
        await PassAsync(PlanLimitsViewModel.ReadInterval);
        _reader.Reads.ShouldBe(2);

        await PassAsync(TimeSpan.FromHours(1));

        _reader.Reads.ShouldBe(2, "each more would spend the limits it reads");
        _vm.FiveHourText.ShouldBeNull();
    }

    [Fact]
    public async Task A_slow_reading_is_not_joined_by_a_second()
    {
        await StartedAsync(Reading());
        _reader.Hold = new TaskCompletionSource();

        for (var i = 0; i < 80; i++)
        {
            _time.Advance(PlanLimitsViewModel.TickInterval);
        }

        await _reader.WaitForReadsAsync(2);
        _reader.Reads.ShouldBe(2);
        _reader.Hold.SetResult();
        await _vm.PendingRead;
    }

    [Fact]
    public async Task Disposing_cancels_the_running_reading_and_ends_the_ticks()
    {
        await StartedAsync(Reading());
        _reader.Hold = new TaskCompletionSource();
        await PassAsync(TimeSpan.Zero);
        _time.Advance(PlanLimitsViewModel.ReadInterval);
        await _reader.WaitForReadsAsync(2);

        _vm.Dispose();
        await _vm.PendingRead;

        _reader.Cancelled.ShouldBeTrue();
        _time.Advance(TimeSpan.FromHours(1));
        _reader.Reads.ShouldBe(2);
    }

    private sealed class FakeReader : IPlanUsageReader
    {
        private int _reads;

        public PlanReading Next { get; set; } = new(PlanReadingKind.NoFigures);

        public Exception? Throws { get; set; }

        /// <summary>When set, a reading waits for it, or for its cancel.</summary>
        public TaskCompletionSource? Hold { get; set; }

        public bool Cancelled { get; private set; }

        public int Reads => Volatile.Read(ref _reads);

        public async Task<PlanReading> ReadAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _reads);
            if (Hold is { } hold)
            {
                try
                {
                    await hold.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    Cancelled = true;
                    throw;
                }
            }

            return Throws is { } throws ? throw throws : Next;
        }

        public async Task WaitForReadsAsync(int count)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (Reads < count)
            {
                DateTime.UtcNow.ShouldBeLessThan(until, "the reading never began");
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
        }
    }
}
