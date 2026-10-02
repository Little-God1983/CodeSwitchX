using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Core.Yard;

/// <summary>
/// What the Yard shows, read for someone who cannot see it: Raven's brain, through the MCP tools. It reports the board as
/// it stands, the same chats and git lines, so an answer never contradicts the screen. Read-only.
/// </summary>
public interface IYardDirectory
{
    /// <summary>Every workspace on the board, in the order it shows them.</summary>
    Task<IReadOnlyList<YardWorkspace>> WorkspacesAsync(CancellationToken ct);

    /// <summary>Every chat the board shows a row for.</summary>
    Task<IReadOnlyList<YardChat>> ChatsAsync(CancellationToken ct);
}

/// <param name="Folders">The folders a .code-workspace file lists, the root first; just the root for a folder workspace.</param>
/// <param name="Git">One line per repository, as the tile shows them; the root's first.</param>
public sealed record YardWorkspace(Guid Id, string Name, string Track, string RootPath, IReadOnlyList<YardFolder> Folders,
    IReadOnlyList<YardGitLine> Git);

/// <param name="Name">What the workspace file calls the folder, else its own name.</param>
public sealed record YardFolder(string Name, string Path);

/// <param name="Folder">The folder's name on a tile with several repositories; null on a tile with one.</param>
/// <param name="Branch">Null when the folder is no repository, or before the first git check.</param>
/// <param name="Changes">"clean" or "3 changed"; null when git could not tell.</param>
public sealed record YardGitLine(string? Folder, string? Branch, string? Changes);

/// <param name="Workspace">The name of the workspace whose tile shows the chat.</param>
/// <param name="StateFor">How long it has been in its state, as the row shows it ("5m", "1h 02m").</param>
/// <param name="ContextFill">How full its context window is, 0 to 1.</param>
/// <param name="Voice">Raven started it, and the app runs it: it can be told something or stopped from here.</param>
/// <param name="SendName">
/// The name another Claude session messages it by while its VS Code tab is open; null when it is not open in one (closed,
/// or run in a terminal or by <c>claude -p</c>), and for a chat the app runs itself.
/// </param>
public sealed record YardChat(string Id, string Title, Guid WorkspaceId, string Workspace, SessionState State, bool NeedsYou, DateTimeOffset StateSince,
    string StateFor, string? Model, string? LastTool, double ContextFill, string? LastNotification, string? Cwd, bool Voice = false,
    string? SendName = null);
