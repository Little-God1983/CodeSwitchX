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
}
