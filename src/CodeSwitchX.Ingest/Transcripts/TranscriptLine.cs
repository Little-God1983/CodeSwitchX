using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Ingest.Transcripts;

public abstract record TranscriptLine(string Type, DateTimeOffset? Timestamp, string? SessionId, string? Cwd);

public sealed record AssistantLine(string Type, DateTimeOffset? Timestamp, string? SessionId, string? Cwd,
    string? MessageId, string? Model, TokenUsage? Usage, bool HasToolUse) : TranscriptLine(Type, Timestamp, SessionId, Cwd);

/// <summary>
/// A prompt or a tool result. <paramref name="BackgroundJob"/> is the job the result started (#332); <paramref name="EndedJob"/>
/// the job a TaskStop left not running (#379), which Claude Code queues no task notification for.
/// </summary>
public sealed record UserLine(string Type, DateTimeOffset? Timestamp, string? SessionId, string? Cwd,
    string? Text, bool IsToolResult, bool IsMeta, bool IsInterrupt = false, string? BackgroundJob = null, string? EndedJob = null)
    : TranscriptLine(Type, Timestamp, SessionId, Cwd);

public sealed record SummaryLine(string Type, DateTimeOffset? Timestamp, string? SessionId, string? Cwd, string Title)
    : TranscriptLine(Type, Timestamp, SessionId, Cwd);

/// <summary>A name the user gave the chat with <c>/rename</c> in Claude Code; a later one replaces an earlier one.</summary>
public sealed record CustomTitleLine(string Type, DateTimeOffset? Timestamp, string? SessionId, string? Cwd, string Title)
    : TranscriptLine(Type, Timestamp, SessionId, Cwd);

/// <summary>Claude Code's task notification as a background job ends (#332): the chat is told of it at its next step.</summary>
public sealed record JobEndLine(string Type, DateTimeOffset? Timestamp, string? SessionId, string? Cwd, string Job)
    : TranscriptLine(Type, Timestamp, SessionId, Cwd);

/// <summary>
/// Claude Code compacted the chat here: the conversation before it is replaced by a summary and the messages it kept,
/// <paramref name="PostTokens"/> together (null when the line does not say). No reply with usage follows until the next turn.
/// </summary>
public sealed record CompactBoundaryLine(string Type, DateTimeOffset? Timestamp, string? SessionId, string? Cwd, long? PostTokens)
    : TranscriptLine(Type, Timestamp, SessionId, Cwd);

public sealed record OtherLine(string Type, DateTimeOffset? Timestamp, string? SessionId, string? Cwd)
    : TranscriptLine(Type, Timestamp, SessionId, Cwd);
