namespace CodeSwitchX.Core.Sessions;

/// <summary>A Claude Code hook payload after normalisation. Unknown events keep <see cref="Signal"/> null.</summary>
public sealed record HookEvent
{
    public required string SessionId { get; init; }
    public required string EventName { get; init; }
    public SessionSignal? Signal { get; init; }
    public required DateTimeOffset At { get; init; }
    public string? Cwd { get; init; }
    public string? TranscriptPath { get; init; }
    public string? ToolName { get; init; }
    public string? ToolUseId { get; init; }
    public string? NotificationType { get; init; }
    public string? Message { get; init; }
    public string? Prompt { get; init; }
    public string? Model { get; init; }

    /// <summary>SessionStart <c>source</c> or SessionEnd <c>reason</c>.</summary>
    public string? Source { get; init; }
    public int? RelayPid { get; init; }
    public IReadOnlyList<ProcessRef> ParentChain { get; init; } = [];
    public string? RawJson { get; init; }
}
