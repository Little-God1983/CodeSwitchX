namespace CodeSwitchX.Core.Yard;

/// <summary>
/// What Raven's brain can do on the Yard, through the MCP tools: start a Claude chat in a workspace's folder and steer or
/// stop it, change the model and effort chats start with, and move between the Yard and a workspace in the Cab. Names are
/// resolved before these are called (<see cref="WorkspaceMatcher"/>); a chat id may be its first characters. A request that
/// cannot be done throws <see cref="YardActionException"/>, whose message is written for the brain to repeat.
/// </summary>
public interface IYardActions
{
    /// <summary>The model and effort a chat starts with when none is said for it.</summary>
    ChatDefaults Defaults { get; }

    /// <summary>
    /// Starts a chat in <paramref name="folder"/>, shown on <paramref name="workspace"/>'s tile, with the prompt as its first
    /// turn. Returns once the chat is under way or has failed to start.
    /// </summary>
    /// <param name="model">A name the alias table knows, or a full id; null for the default.</param>
    /// <param name="effort">An effort level, or how it is said; null for the default.</param>
    Task<StartedChat> StartChatAsync(YardWorkspace workspace, YardFolder folder, string prompt, string? model, string? effort, CancellationToken ct);

    /// <summary>Sends the text to a chat Raven started, as the user's next turn.</summary>
    Task<VoiceChatView> SendToChatAsync(string chatId, string text, CancellationToken ct);

    /// <summary>Changes the defaults; a null leaves that one as it is.</summary>
    Task<ChatDefaults> SetDefaultsAsync(string? model, string? effort, CancellationToken ct);

    /// <summary>
    /// Shows the workspace in the Cab. With a chat Raven started, that chat is handed over to VS Code first: its process
    /// stops, and VS Code opens the same conversation, so only one process writes to it.
    /// </summary>
    Task<string> OpenWorkspaceAsync(YardWorkspace? workspace, string? chatId, CancellationToken ct);

    /// <summary>Shows the Yard.</summary>
    Task BackToYardAsync(CancellationToken ct);

    /// <summary>Stops a chat Raven started; what it was doing is cut off.</summary>
    Task<string> StopChatAsync(string chatId, CancellationToken ct);

    /// <summary>The chats Raven started that still run.</summary>
    IReadOnlyList<VoiceChatView> VoiceChats { get; }
}

/// <summary>A request the Yard cannot carry out; the message says why, in words for the user.</summary>
public sealed class YardActionException(string message) : Exception(message);

/// <param name="Model">The alias name or id chats start with; null for Claude Code's own default.</param>
/// <param name="Effort">The effort level; null for Claude Code's own default.</param>
public sealed record ChatDefaults(string? Model, string? Effort);

/// <param name="Note">What the user should know about how it runs, or null: a model that cannot run in auto mode.</param>
public sealed record StartedChat(VoiceChatView Chat, string? Note);

/// <summary>A chat Raven started, as the brain is told about it.</summary>
/// <param name="Folder">The folder it runs in.</param>
/// <param name="Model">The full model id, or null for Claude Code's default.</param>
/// <param name="Working">A turn runs.</param>
/// <param name="PermissionMode">The mode Claude Code reports it runs in ("auto"); null until it said.</param>
public sealed record VoiceChatView(string Id, Guid WorkspaceId, string Workspace, string Folder, string? Model, string? Effort, bool Working,
    string? PermissionMode);
