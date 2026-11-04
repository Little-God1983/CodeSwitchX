namespace CodeSwitchX.Core.Usage;

/// <summary>One limit of the user's plan: how much of it is used, and when it starts again; null when the text gave no time.</summary>
public sealed record PlanLimit(int Percent, DateTimeOffset? ResetsAt);

/// <summary>The real limits of the user's plan (#370); a limit that could not be read is null.</summary>
public sealed record PlanLimits(PlanLimit? FiveHour, PlanLimit? Week)
{
    public bool HasAny => FiveHour is not null || Week is not null;
}

/// <summary>How a reading of the plan's limits ended.</summary>
public enum PlanReadingKind
{
    /// <summary>Claude Code answered with at least one limit.</summary>
    Read,

    /// <summary>Claude Code answered without any: no subscription, not logged in, or a text this app cannot read.</summary>
    NoFigures,

    /// <summary>No answer: no Claude Code, or it failed or took too long. What was last read still holds.</summary>
    Failed,

    /// <summary>The command reached the model instead of running on its own, which spends the very limits it reads: never read again.</summary>
    SpentATurn,
}

public sealed record PlanReading(PlanReadingKind Kind, PlanLimits? Limits = null);

/// <summary>Reads the plan's limits. Takes seconds, so never on the UI thread.</summary>
public interface IPlanUsageReader
{
    /// <summary>Never throws, but for a cancel of <paramref name="ct"/>.</summary>
    Task<PlanReading> ReadAsync(CancellationToken ct);
}
