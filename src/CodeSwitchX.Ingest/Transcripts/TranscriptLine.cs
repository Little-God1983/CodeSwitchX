using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Ingest.Transcripts;

public abstract record TranscriptLine(string Type, DateTimeOffset? Timestamp, string? SessionId, string? Cwd);

public sealed record AssistantLine(string Type, DateTimeOffset? Timestamp, string? SessionId, string? Cwd,
    string? MessageId, string? Model, TokenUsage? Usage, bool HasToolUse) : TranscriptLine(Type, Timestamp, SessionId, Cwd);

public sealed record UserLine(string Type, DateTimeOffset? Timestamp, string? SessionId, string? Cwd,
    string? Text, bool IsToolResult, bool IsMeta) : TranscriptLine(Type, Timestamp, SessionId, Cwd);

public sealed record SummaryLine(string Type, DateTimeOffset? Timestamp, string? SessionId, string? Cwd, string Title)
    : TranscriptLine(Type, Timestamp, SessionId, Cwd);

public sealed record OtherLine(string Type, DateTimeOffset? Timestamp, string? SessionId, string? Cwd)
    : TranscriptLine(Type, Timestamp, SessionId, Cwd);
