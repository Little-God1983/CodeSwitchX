using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Telemetry;

namespace CodeSwitchX.Telemetry.Tests;

public class UsageAggregatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 30, 0, TimeSpan.Zero);
    private readonly UsageAggregator _aggregator = new(new PricingTable([new PricingRule { Model = "m", InputPerM = 1m, OutputPerM = 10m, CacheWritePerM = 1m, CacheReadPerM = 0.1m }]));

    private static UsageBucket Bucket(string session, DateTimeOffset minute, long input, long output = 0) =>
        new() { SessionId = session, Model = "m", MinuteUtc = minute, Input = input, Output = output };

    [Fact]
    public void Sum_adds_tokens_and_cost_across_buckets()
    {
        var totals = _aggregator.Sum([Bucket("a", Now, 1_000_000, 100_000), Bucket("b", Now, 1_000_000)]);

        totals.Tokens.Input.ShouldBe(2_000_000);
        totals.Tokens.Output.ShouldBe(100_000);
        totals.Cost.ShouldBe(2m + 1m);
    }

    [Fact]
    public void Window_keeps_buckets_newer_than_now_minus_span()
    {
        var buckets = new[]
        {
            Bucket("a", Now.AddHours(-6), 1),
            Bucket("a", Now.AddHours(-5), 2),
            Bucket("a", Now.AddHours(-4), 4),
            Bucket("a", Now, 8),
        };

        _aggregator.Window(buckets, Now, TimeSpan.FromHours(5)).Tokens.Input.ShouldBe(14);
    }

    [Fact]
    public void Today_uses_the_local_calendar_day()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        // 2026-09-23 00:30 Berlin is 2026-09-22 22:30 UTC: still "today" in Berlin, "yesterday" in UTC.
        var buckets = new[]
        {
            Bucket("a", new DateTimeOffset(2026, 9, 22, 21, 59, 0, TimeSpan.Zero), 1),
            Bucket("a", new DateTimeOffset(2026, 9, 22, 22, 30, 0, TimeSpan.Zero), 2),
            Bucket("a", Now, 4),
        };

        _aggregator.Today(buckets, Now, berlin).Tokens.Input.ShouldBe(6);
        _aggregator.Today(buckets, Now, TimeZoneInfo.Utc).Tokens.Input.ShouldBe(4);
    }

    [Fact]
    public void RateSeries_is_oldest_first_zero_filled_and_counts_all_token_kinds()
    {
        var buckets = new[]
        {
            Bucket("a", Now.AddMinutes(-2), 5, 1),
            new UsageBucket { SessionId = "a", Model = "m", MinuteUtc = Now, Input = 1, CacheRead = 9 },
            Bucket("a", Now.AddMinutes(-10), 100),
        };

        var series = _aggregator.RateSeries(buckets, Now, minutes: 5);

        series.ShouldBe([0, 0, 6, 0, 10]);
    }

    [Fact]
    public void Today_starts_at_local_midnight_even_on_a_dst_changeover_day()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        // 2026-03-29: Berlin springs forward at 02:00. Midnight was UTC+1 (23:00Z the evening before); noon is UTC+2 (10:00Z).
        var noon = new DateTimeOffset(2026, 3, 29, 10, 0, 0, TimeSpan.Zero);
        var buckets = new[]
        {
            Bucket("a", new DateTimeOffset(2026, 3, 28, 22, 30, 0, TimeSpan.Zero), 1), // 23:30 local: yesterday
            Bucket("a", new DateTimeOffset(2026, 3, 28, 23, 30, 0, TimeSpan.Zero), 2), // 00:30 local: today
            Bucket("a", noon, 4),
        };

        _aggregator.Today(buckets, noon, berlin).Tokens.Input.ShouldBe(6);
    }

    [Fact]
    public void BySession_groups_totals()
    {
        var totals = _aggregator.BySession([Bucket("a", Now, 1), Bucket("b", Now, 2), Bucket("a", Now.AddMinutes(-1), 3)]);

        totals["a"].Tokens.Input.ShouldBe(4);
        totals["b"].Tokens.Input.ShouldBe(2);
    }
}
