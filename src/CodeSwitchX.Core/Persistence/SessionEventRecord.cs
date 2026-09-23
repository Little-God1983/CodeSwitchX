namespace CodeSwitchX.Core.Persistence;

public sealed class SessionEventRecord
{
    public long Id { get; set; }
    public string SessionId { get; set; } = string.Empty;

    /// <summary>The hook event name, e.g. <c>PreToolUse</c>.</summary>
    public string Kind { get; set; } = string.Empty;
    public string? ToolName { get; set; }
    public DateTimeOffset At { get; set; }

    /// <summary>Raw payload; only stored when the user opted in.</summary>
    public string? PayloadJson { get; set; }
}
