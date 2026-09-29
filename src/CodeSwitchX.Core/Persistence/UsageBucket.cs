using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Core.Persistence;

/// <summary>Tokens of one session and model within one minute; the sums the batch, the store and telemetry keep.</summary>
public sealed class UsageBucket
{
    public string SessionId { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public DateTimeOffset MinuteUtc { get; set; }
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheWrite { get; set; }
    public long CacheRead { get; set; }

    public TokenUsage Tokens => new(Input, Output, CacheWrite, CacheRead);

    /// <summary>The one place that adds usage up, so a new token kind reaches every sum.</summary>
    public void Add(TokenUsage tokens)
    {
        Input += tokens.Input;
        Output += tokens.Output;
        CacheWrite += tokens.CacheWrite;
        CacheRead += tokens.CacheRead;
    }

    public void Add(UsageBucket delta) => Add(delta.Tokens);

    public static DateTimeOffset FloorToMinute(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
    }
}
