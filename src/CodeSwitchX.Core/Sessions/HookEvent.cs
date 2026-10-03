namespace CodeSwitchX.Core.Sessions;

/// <summary>A Claude Code hook payload after normalisation. Unknown events keep <see cref="Signal"/> null.</summary>
public sealed record HookEvent
{
    public required string SessionId { get; init; }
    public required string EventName { get; init; }
    public SessionSignal? Signal { get; init; }

    /// <summary>
    /// True for an event the parser knows and leaves the state alone on purpose (SessionStart after compaction, a
    /// Notification that needs no one, SubagentStop). An event without a signal that is not marked so is logged as unknown.
    /// </summary>
    public bool Informational { get; init; }
    public required DateTimeOffset At { get; init; }
    public string? Cwd { get; init; }
    public string? TranscriptPath { get; init; }
    public string? ToolName { get; init; }
    public string? ToolUseId { get; init; }

    /// <summary>
    /// A fingerprint of the tool's input, the relay's: the same in a tool use's PreToolUse and in the PermissionRequest it
    /// raises, which names no tool use of its own. Null without a tool input, and from a relay older than permission prompts.
    /// </summary>
    public string? ToolInputHash { get; init; }

    /// <summary>
    /// The chat's project folder (Claude Code's <c>CLAUDE_PROJECT_DIR</c>), the relay's on a permission prompt: <see cref="Cwd"/>
    /// can be a folder in it the chat moved to. Null from a relay older than #107.
    /// </summary>
    public string? ProjectDir { get; init; }

    /// <summary>The sub-agent the event comes from; null for the chat's main agent. Sub-agents send hooks with the parent's session id.</summary>
    public string? AgentId { get; init; }
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
