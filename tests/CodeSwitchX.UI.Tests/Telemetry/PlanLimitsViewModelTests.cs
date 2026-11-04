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

        _reader.Reads.ShouldBeLessThan(2 + PlanLimitsViewModel.MaxResetRetries + 12);
        _vm.FiveHourText.ShouldBe("5h reading again…");
    }

    [Fact]
    public async Task A_stuck_reset_does_not_make_the_other_limit_s_reset_wait()
    {
        var stale = Start.AddHours(-1);
        await StartedAsync(Reading(inFive: TimeSpan.FromMinutes(18), weekAt: stale)); // the week's reset stays past, and its retries run out
        await PassAsync(TimeSpan.FromMinutes(17));
        _reader.Next = Reading(fiveHour: 0, inFive: TimeSpan.FromHours(5), weekAt: stale);

        await PassAsync(TimeSpan.FromMinutes(1));

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
