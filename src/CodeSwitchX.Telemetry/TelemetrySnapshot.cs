namespace CodeSwitchX.Telemetry;

public sealed record TelemetrySnapshot(UsageTotals Today, UsageTotals FiveHours, long[] RatePerMinute, DateTimeOffset AsOf)
{
    public static TelemetrySnapshot Empty(DateTimeOffset asOf) => new(UsageTotals.Zero, UsageTotals.Zero, new long[60], asOf);
}

public sealed record TelemetryUpdated(TelemetrySnapshot Snapshot);
