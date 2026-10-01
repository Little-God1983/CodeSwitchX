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

    /// <summary>
    /// The last turn ended on an API error (usage limit, overload, prompt too long: Claude Code's StopFailure) rather than
    /// with its work done; the state is Idle all the same. Until the next prompt or turn end. Not persisted.
    /// </summary>
    public bool TurnFailed { get; init; }

    /// <summary>Engine-wide monotonic counter stamped on every published change so consumers can drop stale snapshots. Not persisted.</summary>
    public long Version { get; init; }
    public int? ClaudePid { get; init; }

    /// <summary>
    /// The folders of the VS Code window the chat runs in (<see cref="Workspaces.IIdeWindows"/>), null when it runs in none
    /// or was never looked up. Persisted: the window still tells the chat's tile once its claude is gone.
    /// </summary>
    public IReadOnlyList<string>? WindowFolders { get; init; }
    public TokenUsage LatestContext { get; init; }

    /// <summary>
    /// Whether anything was ever said in this session: a prompt gives it a title, a reply leaves token usage, a tool use
    /// names its tool. Claude Code starts and quits without a prompt whenever a VS Code window loads, and such a session
    /// has none of these. All three are persisted, so a real chat restored after a restart still counts.
    /// </summary>
    public bool HeldConversation => Title is not null || LatestContext != TokenUsage.Zero || LastToolName is not null;

    /// <summary>
    /// Whether the session is a chat the Yard shows and counts: once it held a conversation, or while it works or waits for
    /// the user. A session that only sits there (Starting, Idle, Stale) or is over without ever having held one is no chat
    /// yet: Claude Code starts one each time a VS Code window loads, and quits it again seconds or a minute later without a
    /// prompt. A prompt makes it Working at once, so a chat typed into a fresh panel shows before its reply.
    /// </summary>
    public bool ShowsAsChat => HeldConversation || State is SessionState.Working or SessionState.Waiting;
}
