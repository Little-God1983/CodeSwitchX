namespace CodeSwitchX.Conductor;

/// <summary>
/// Runs the Claude chats Raven starts: each a <c>claude -p</c> that the app owns, working in a folder the user named, with
/// the user's turns written to it. They are ordinary Claude Code sessions: their hooks report them to the Yard like any
/// other chat, and VS Code can open them to go on by hand once their process is stopped.
/// </summary>
public interface IAgentLauncher : IDisposable
{
    /// <summary>
    /// Starts the chat with the prompt as its first turn, and returns once it has begun to answer, has failed, or has not
    /// said either within <see cref="ClaudeAgentLauncher.StartWait"/> (a model that thinks long first counts as started).
    /// </summary>
    /// <exception cref="Core.Yard.YardActionException">Claude Code is not there, the folder is gone, or it could not be started.</exception>
    Task<AgentStart> StartAsync(AgentRequest request, CancellationToken ct);

    /// <summary>Writes the text to the chat as the user's next turn; one sent while a turn runs is taken after it.</summary>
    /// <exception cref="Core.Yard.YardActionException">No chat of these that runs has the id.</exception>
    Task<AgentChat> SendAsync(string chatId, string text, CancellationToken ct);

    /// <summary>
    /// Stops the chat's process: one between turns ends as Claude Code ends a session, one in the middle of a turn is
    /// killed. Returns the chat as it was, whose <see cref="AgentChat.Working"/> says whether a turn was cut off.
    /// </summary>
    /// <exception cref="Core.Yard.YardActionException">No chat of these that runs has the id.</exception>
    Task<AgentChat> StopAsync(string chatId, CancellationToken ct);

    /// <summary>The chat with this id, or with an id that starts with it; null for none, or for more than one.</summary>
    AgentChat? Find(string idOrPrefix);

    /// <summary>The chats that run.</summary>
    IReadOnlyList<AgentChat> Chats { get; }

    /// <summary>A chat started, began or ended a turn, reported its mode, or ended. On the thread that saw it.</summary>
    event Action<AgentChat>? Changed;

    /// <summary>A chat that had started failed a turn or stopped by itself; the text says why. On the thread that saw it.</summary>
    event Action<AgentChat, string>? Failed;
}

/// <param name="Id">The session id to start it with: the caller picks it, so the chat can be placed before its first event.</param>
/// <param name="Workspace">The name of the workspace whose tile shows it.</param>
/// <param name="Model">A full model id; null for Claude Code's default.</param>
/// <param name="Effort">An effort level (<c>--effort</c>); null for Claude Code's default.</param>
public sealed record AgentRequest(string Id, Guid WorkspaceId, string Workspace, string Folder, string Prompt, string? Model, string? Effort);

/// <param name="Working">A turn runs.</param>
/// <param name="PermissionMode">The mode it runs in as Claude Code reports it; null until it did.</param>
/// <param name="Stopped">It ended because the app stopped it (stop_chat, a hand-over to VS Code), not on its own.</param>
public sealed record AgentChat(string Id, Guid WorkspaceId, string Workspace, string Folder, string? Model, string? Effort, bool Working,
    string? PermissionMode, bool Ended, bool Stopped = false);

/// <param name="Failure">Why it did not start, in words for the user; null when it did.</param>
public sealed record AgentStart(AgentChat Chat, string? Failure);
