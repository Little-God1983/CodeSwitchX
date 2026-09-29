namespace CodeSwitchX.Core.Sessions;

public sealed record SessionSnapshot
{
    public required string SessionId { get; init; }
    public Guid? WorkspaceId { get; init; }
    public string? Title { get; init; }
    public bool TitleLocked { get; init; }
    public required SessionState State { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset LastEventAt { get; init; }
    public required DateTimeOffset StateSince { get; init; }
    public string? Cwd { get; init; }
    public string? TranscriptPath { get; init; }
    public string? Model { get; init; }
    public string? LastToolName { get; init; }
    public string? LastNotification { get; init; }

    /// <summary>True while every fact came from transcript inference and no hook event was ever seen.</summary>
    public bool Inferred { get; init; }

    /// <summary>True once a hook event arrived in this process; not persisted, so restored sessions follow inference until hooks speak again.</summary>
    public bool HookSeen { get; init; }

    /// <summary>Transcript ends with a tool call awaiting its result; drives the inferred Working-to-Waiting decay. Not persisted.</summary>
    public bool AwaitingToolResult { get; init; }

    /// <summary>Engine-wide monotonic counter stamped on every published change so consumers can drop stale snapshots. Not persisted.</summary>
    public long Version { get; init; }
    public int? ClaudePid { get; init; }
    public TokenUsage LatestContext { get; init; }
}
