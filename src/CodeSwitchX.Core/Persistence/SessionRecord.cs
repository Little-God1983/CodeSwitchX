using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Core.Persistence;

public sealed class SessionRecord
{
    public string Id { get; set; } = string.Empty;
    public Guid? WorkspaceId { get; set; }
    public string? Title { get; set; }
    public bool TitleLocked { get; set; }
    public SessionState State { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastEventAt { get; set; }
    public DateTimeOffset StateSince { get; set; }
    public string? Cwd { get; set; }
    public string? TranscriptPath { get; set; }
    public string? Model { get; set; }
    public string? LastToolName { get; set; }
    public string? LastNotification { get; set; }
    public bool Inferred { get; set; }
    public int? ClaudePid { get; set; }
    public long ContextInput { get; set; }
    public long ContextOutput { get; set; }
    public long ContextCacheWrite { get; set; }
    public long ContextCacheRead { get; set; }

    public static SessionRecord FromSnapshot(SessionSnapshot s)
    {
        var record = new SessionRecord { Id = s.SessionId };
        record.UpdateFrom(s);
        return record;
    }

    public void UpdateFrom(SessionSnapshot s)
    {
        WorkspaceId = s.WorkspaceId;
        Title = s.Title;
        TitleLocked = s.TitleLocked;
        State = s.State;
        StartedAt = s.StartedAt;
        LastEventAt = s.LastEventAt;
        StateSince = s.StateSince;
        Cwd = s.Cwd;
        TranscriptPath = s.TranscriptPath;
        Model = s.Model;
        LastToolName = s.LastToolName;
        LastNotification = s.LastNotification;
        Inferred = s.Inferred;
        ClaudePid = s.ClaudePid;
        ContextInput = s.LatestContext.Input;
        ContextOutput = s.LatestContext.Output;
        ContextCacheWrite = s.LatestContext.CacheWrite;
        ContextCacheRead = s.LatestContext.CacheRead;
    }

    public SessionSnapshot ToSnapshot() => new()
    {
        SessionId = Id,
        WorkspaceId = WorkspaceId,
        Title = Title,
        TitleLocked = TitleLocked,
        State = State,
        StartedAt = StartedAt,
        LastEventAt = LastEventAt,
        StateSince = StateSince,
        Cwd = Cwd,
        TranscriptPath = TranscriptPath,
        Model = Model,
        LastToolName = LastToolName,
        LastNotification = LastNotification,
        Inferred = Inferred,
        ClaudePid = ClaudePid,
        LatestContext = new TokenUsage(ContextInput, ContextOutput, ContextCacheWrite, ContextCacheRead),
    };
}
