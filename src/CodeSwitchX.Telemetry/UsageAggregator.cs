using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Telemetry;

public sealed class UsageAggregator
{
    private readonly PricingTable _pricing;

    public UsageAggregator(PricingTable pricing)
    {
        _pricing = pricing;
    }

    public UsageTotals Sum(IEnumerable<UsageBucket> buckets)
    {
        var tokens = TokenUsage.Zero;
        var cost = 0m;
        foreach (var bucket in buckets)
        {
            var usage = new TokenUsage(bucket.Input, bucket.Output, bucket.CacheWrite, bucket.CacheRead);
            tokens += usage;
            cost += CostEstimator.Estimate(usage, _pricing.Find(bucket.Model));
        }

        return new UsageTotals(tokens, cost);
    }

    public UsageTotals Window(IEnumerable<UsageBucket> buckets, DateTimeOffset now, TimeSpan span)
    {
        var from = now - span;
        return Sum(buckets.Where(b => b.MinuteUtc >= from && b.MinuteUtc <= now));
    }

    public UsageTotals Today(IEnumerable<UsageBucket> buckets, DateTimeOffset now, TimeZoneInfo zone)
    {
        // Midnight's own offset, not the current one: on a DST changeover day they differ by an hour. When the clocks fall
        // back to midnight it comes twice, and the day starts at the first one, which has the larger offset.
        var localDate = TimeZoneInfo.ConvertTime(now, zone).Date;
        var offset = zone.IsAmbiguousTime(localDate) ? zone.GetAmbiguousTimeOffsets(localDate).Max() : zone.GetUtcOffset(localDate);
        var startUtc = new DateTimeOffset(localDate, offset).ToUniversalTime();
        return Sum(buckets.Where(b => b.MinuteUtc >= startUtc && b.MinuteUtc <= now));
    }

    /// <summary>Total tokens per minute for the last <paramref name="minutes"/> minutes, oldest first, current minute last.</summary>
    public long[] RateSeries(IEnumerable<UsageBucket> buckets, DateTimeOffset now, int minutes)
    {
        var series = new long[minutes];
        var currentMinute = UsageBucket.FloorToMinute(now);
        foreach (var bucket in buckets)
        {
            var age = (int)Math.Floor((currentMinute - UsageBucket.FloorToMinute(bucket.MinuteUtc)).TotalMinutes);
            if (age < 0 || age >= minutes)
            {
                continue;
            }

            series[minutes - 1 - age] += bucket.Input + bucket.Output + bucket.CacheWrite + bucket.CacheRead;
        }

        return series;
    }

    public IReadOnlyDictionary<string, UsageTotals> BySession(IEnumerable<UsageBucket> buckets) =>
        buckets.GroupBy(b => b.SessionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Sum(g), StringComparer.Ordinal);
}
