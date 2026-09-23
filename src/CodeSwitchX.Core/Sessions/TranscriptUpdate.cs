namespace CodeSwitchX.Core.Sessions;

/// <summary>Facts the transcript indexer extracted from newly appended JSONL lines of one session.</summary>
public sealed record TranscriptUpdate
{
    public required string SessionId { get; init; }
    public required string TranscriptPath { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public string? Title { get; init; }
    public string? Cwd { get; init; }
    public string? Model { get; init; }
    public DateTimeOffset? LastActivityAt { get; init; }
    public IReadOnlyList<UsageDelta> Usage { get; init; } = [];
    public TokenUsage? LatestContext { get; init; }

    /// <summary>Best-effort state signal, only honoured while the session has never received a hook event.</summary>
    public SessionSignal? InferredSignal { get; init; }

    /// <summary>True when the transcript was last written longer ago than the history window; such updates never create sessions.</summary>
    public bool Historical { get; init; }

    /// <summary>Whether the transcript ends with an assistant tool call that has no result yet; null when unknown (sub-agent files).</summary>
    public bool? PendingToolUse { get; init; }

    /// <summary>The newest lines end with a user interrupt (Esc); Claude Code fires no Stop hook for that, so this overrides hook evidence.</summary>
    public bool Interrupted { get; init; }

    /// <summary>The byte offset this update brings the file to; persisted together with the usage it covers.</summary>
    public Persistence.TranscriptCursor? Cursor { get; init; }
}
