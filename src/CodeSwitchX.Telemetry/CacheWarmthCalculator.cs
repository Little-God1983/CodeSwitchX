namespace CodeSwitchX.Telemetry;

public enum CacheWarmth
{
    Unknown,
    Hot,
    Warm,
    Cold,
}

/// <summary>
/// How much of its hour a chat's prompt cache has left (#331). Measured on the transcripts: every cache write was a 1-hour
/// one, and the cache was still there at 59.4 minutes and gone at 60.8. A cache lost early is not something a timer can tell.
/// </summary>
public static class CacheWarmthCalculator
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    /// <summary>From here on the cache counts as warm: time enough to go on with the chat, or to finish the work in it.</summary>
    public static readonly TimeSpan WarmLeft = TimeSpan.FromMinutes(20);

    public static TimeSpan Left(DateTimeOffset lastRequest, DateTimeOffset now)
    {
        var left = Lifetime - (now - lastRequest);
        return left < TimeSpan.Zero ? TimeSpan.Zero : left > Lifetime ? Lifetime : left;
    }

    public static CacheWarmth Level(TimeSpan left) =>
        left <= TimeSpan.Zero ? CacheWarmth.Cold
        : left <= WarmLeft ? CacheWarmth.Warm
        : CacheWarmth.Hot;

    /// <summary>The share of the gauge to fill. A cold cache fills it, in its own colour, so it does not look like a chat without data.</summary>
    public static double Fill(TimeSpan left) => left <= TimeSpan.Zero ? 1.0 : left / Lifetime;
}
