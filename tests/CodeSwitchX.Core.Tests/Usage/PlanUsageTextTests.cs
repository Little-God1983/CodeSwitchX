using CodeSwitchX.Core.Usage;

namespace CodeSwitchX.Core.Tests.Usage;

/// <summary>Pins the text of <c>claude -p "/usage"</c>, which is written for people and may change (#370). The samples are captured from Claude Code 2.1.316.</summary>
public class PlanUsageTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>On the hour Claude Code leaves the minutes out.</summary>
    private const string OnTheHour = """
        You are currently using your subscription to power your Claude Code usage

        Current session: 1% used · resets Nov 4, 10pm (Europe/Berlin)
        Current week (all models): 46% used · resets Nov 9, 6am (Europe/Berlin)
        Current week (Fable): 11% used · resets Nov 9, 6am (Europe/Berlin)

        What's contributing to your usage?
        Approximate · sessions on this machine only · shares overlap

        Last 24h · 1519 requests · 10 sessions
          84%  Give "general-purpose" subagents a smaller scope
        """;

    private const string WithMinutes = """
        Current session: 54% used · resets Nov 3, 10:39pm (Europe/Berlin)
        Current week (all models): 38% used · resets Nov 9, 5:59am (Europe/Berlin)
        Current week (Fable): 11% used · resets Nov 9, 5:59am (Europe/Berlin)
        """;

    /// <summary>With an API key, and with nobody logged in: captured with a fake key and an empty config folder.</summary>
    private const string NoSubscription = """
        Total cost:            $0.0000
        Total duration (API):  0s
        Total duration (wall): 0s
        Total code changes:    0 lines added, 0 lines removed
        Usage:                 0 input, 0 output, 0 cache read, 0 cache write
        """;

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Both_limits_are_read_from_a_reset_on_the_hour()
    {
        var limits = PlanUsageText.Parse(OnTheHour, Now);

        limits.FiveHour.ShouldBe(new PlanLimit(1, Utc(2026, 11, 4, 21)));
        limits.Week.ShouldBe(new PlanLimit(46, Utc(2026, 11, 9, 5)));
    }

    [Fact]
    public void Both_limits_are_read_from_a_reset_with_minutes()
    {
        var limits = PlanUsageText.Parse(WithMinutes, Now);

        limits.FiveHour.ShouldBe(new PlanLimit(54, Utc(2026, 11, 3, 21, 39)));
        limits.Week.ShouldBe(new PlanLimit(38, Utc(2026, 11, 9, 4, 59)));
    }

    [Fact]
    public void Windows_line_ends_are_read_too()
    {
        var limits = PlanUsageText.Parse(WithMinutes.ReplaceLineEndings("\r\n"), Now);

        limits.FiveHour!.Percent.ShouldBe(54);
        limits.Week!.Percent.ShouldBe(38);
    }

    [Fact]
    public void The_week_line_of_one_model_is_not_taken_for_the_week_of_all_models()
    {
        var limits = PlanUsageText.Parse("Current week (Fable): 11% used · resets Nov 9, 6am (Europe/Berlin)", Now);

        limits.Week.ShouldBeNull();
    }

    [Fact]
    public void The_zone_comes_from_the_text_and_not_from_this_machine()
    {
        var limits = PlanUsageText.Parse("Current session: 5% used · resets Nov 4, 10pm (Asia/Tokyo)", Now);

        limits.FiveHour!.ResetsAt.ShouldBe(Utc(2026, 11, 4, 13));
    }

    [Fact]
    public void Summer_time_is_the_zones_own()
    {
        var limits = PlanUsageText.Parse("Current week (all models): 5% used · resets Jul 6, 6am (Europe/Berlin)", new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

        limits.Week!.ResetsAt.ShouldBe(Utc(2026, 7, 6, 4));
    }

    [Fact]
    public void A_reset_across_New_Year_lands_in_the_next_year()
    {
        var limits = PlanUsageText.Parse("Current week (all models): 5% used · resets Jan 2, 6am (Europe/Berlin)", Utc(2026, 12, 30, 12));

        limits.Week!.ResetsAt.ShouldBe(Utc(2027, 1, 2, 5));
    }

    [Fact]
    public void A_reset_just_passed_over_New_Year_stays_in_the_old_year()
    {
        var limits = PlanUsageText.Parse("Current session: 5% used · resets Dec 31, 11pm (Europe/Berlin)", Utc(2027, 1, 1, 0));

        limits.FiveHour!.ResetsAt.ShouldBe(Utc(2026, 12, 31, 22));
    }

    [Fact]
    public void A_leap_day_is_read_in_a_year_that_has_one()
    {
        var limits = PlanUsageText.Parse("Current week (all models): 5% used · resets Feb 29, 6am (Europe/Berlin)", Utc(2028, 2, 25, 0));

        limits.Week!.ResetsAt.ShouldBe(Utc(2028, 2, 29, 5));
    }

    [Fact]
    public void A_line_with_no_reset_keeps_its_percent()
    {
        // What a window nobody has used yet looks like is not known; the percent on its own is still true.
        var limits = PlanUsageText.Parse("Current session: 0% used", Now);

        limits.FiveHour.ShouldBe(new PlanLimit(0, null));
    }

    [Theory]
    [InlineData("Current session: about half used")] // no percent
    [InlineData("Current session: 54% used · resets soon (Europe/Berlin)")] // a time it cannot read
    [InlineData("Current session: 54% used · resets Nov 4, 10pm")] // no zone
    [InlineData("Current session: 54% used · resets Nov 4, 10pm (Mars/Olympus)")] // a zone it cannot read
    [InlineData("Current session: 54% left · resets Nov 4, 10pm (Europe/Berlin)")] // says something else
    public void A_changed_line_gives_no_figure_rather_than_a_wrong_one(string line)
    {
        var limits = PlanUsageText.Parse(line, Now);

        limits.FiveHour.ShouldBeNull();
    }

    [Fact]
    public void One_limit_stands_when_the_other_cannot_be_read()
    {
        var limits = PlanUsageText.Parse("Current session: 54% used · resets soon (Europe/Berlin)\nCurrent week (all models): 38% used · resets Nov 9, 6am (Europe/Berlin)", Now);

        limits.FiveHour.ShouldBeNull();
        limits.Week!.Percent.ShouldBe(38);
    }

    [Theory]
    [InlineData("")]
    [InlineData(NoSubscription)]
    public void A_text_without_the_lines_has_no_limits(string text)
    {
        var limits = PlanUsageText.Parse(text, Now);

        limits.HasAny.ShouldBeFalse();
    }
}
