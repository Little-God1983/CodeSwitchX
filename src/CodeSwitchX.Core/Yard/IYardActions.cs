namespace CodeSwitchX.Core.Yard;

/// <summary>
/// What Raven's brain can do on the Yard, through the MCP tools: open, stop and close Claude chats in a workspace's VS Code, change the
/// model and effort chats start with, and move between the Yard and a workspace in the Cab. Names are resolved before
/// these are called (<see cref="WorkspaceMatcher"/>). A request that cannot be done throws <see cref="YardActionException"/>,
/// whose message is written for the brain to repeat.
/// </summary>
public interface IYardActions
{
    /// <summary>The model and effort a chat starts with when none is said for it.</summary>
    ChatDefaults Defaults { get; }

    /// <summary>
    /// Opens a new, empty chat in a tab of <paramref name="workspace"/>'s VS Code window, shown on its tile. Returns once the
    /// chat runs; its first message is sent to it by name (<see cref="VoiceChatView.SendTo"/>).
    /// </summary>
    /// <param name="folder">The folder the user named for it; null for wherever VS Code starts a chat.</param>
    /// <param name="model">A name the alias table knows, or a full id; null for the default.</param>
    /// <param name="effort">An effort level, or how it is said; null for the default.</param>
    Task<VoiceChatView> StartChatAsync(YardWorkspace workspace, YardFolder? folder, string? model, string? effort, CancellationToken ct);

    /// <summary>
    /// Closes the chat's tab in VS Code, any chat open there, Raven's or not, and takes its row off the tile at once.
    /// Returns once it has ended; its conversation stays in VS Code's session list.
    /// </summary>
    Task<string> CloseChatAsync(YardChat chat, CancellationToken ct);

    /// <summary>
    /// Stops the chat's running turn at its next tool step, keeping the chat. Returns what came of it: stopped, stopping
    /// at its next step (it writes, or is in a long step), or done before the stop came.
    /// </summary>
    Task<string> StopChatAsync(YardChat chat, CancellationToken ct);

    /// <summary>Changes the defaults; a null leaves that one as it is.</summary>
    Task<ChatDefaults> SetDefaultsAsync(string? model, string? effort, CancellationToken ct);

    /// <summary>Shows the workspace in the Cab.</summary>
    Task<string> OpenWorkspaceAsync(YardWorkspace workspace, CancellationToken ct);

    /// <summary>Shows the Yard.</summary>
    Task BackToYardAsync(CancellationToken ct);

    /// <summary>Whether Raven started the chat while this app runs. Any thread.</summary>
    bool StartedByRaven(string chatId);
}

/// <summary>A request the Yard cannot carry out; the message says why, in words for the user.</summary>
public sealed class YardActionException(string message) : Exception(message);

/// <param name="Model">The alias name or id chats start with; null for Claude Code's own default.</param>
/// <param name="Effort">The effort level; null for Claude Code's own default.</param>
public sealed record ChatDefaults(string? Model, string? Effort);

/// <summary>A chat Raven started, as the brain is told about it.</summary>
/// <param name="Folder">The folder it runs in.</param>
/// <param name="Model">The full model id, or null for VS Code's own.</param>
/// <param name="Effort">The effort level, or null for VS Code's own.</param>
/// <param name="SendTo">The name it is messaged by (<c>SendMessage</c>).</param>
public sealed record VoiceChatView(string Id, Guid WorkspaceId, string Workspace, string Folder, string? Model, string? Effort, string SendTo);
