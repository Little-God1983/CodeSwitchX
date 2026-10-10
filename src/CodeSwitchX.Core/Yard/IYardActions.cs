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
    /// <param name="askedIn">
    /// The Raven chat the request came from, as its brain names it (<see cref="YardMcp.ChatKey"/>); null for a caller
    /// that is no Raven chat. Asked in another window's chat, or chat 0, the user is moved to the workspace's chat, where
    /// the new chat's news comes (#180).
    /// </param>
    Task<VoiceChatView> StartChatAsync(YardWorkspace workspace, YardFolder? folder, string? model, string? effort, string? askedIn, CancellationToken ct);

    /// <summary>
    /// Closes the chat's tab in VS Code, any chat open there, Raven's or not, and takes its row off the tile at once.
    /// Returns once it has ended; its conversation stays in VS Code's session list.
    /// </summary>
    Task<string> CloseChatAsync(YardChat chat, CancellationToken ct);

    /// <summary>
    /// Compacts the chat as <c>/compact</c> in its tab would (#226): its tab closes, Claude Code compacts the conversation,
    /// and the tab opens again. Returns once it is done, with what Raven says of it; a long one goes on after a minute, the
    /// answer says so, and what comes of it is written in the chat's window chat in Raven's panel.
    /// </summary>
    /// <param name="keep">What the summary is to keep ("the test plan"); null for a plain compaction.</param>
    Task<string> CompactChatAsync(YardChat chat, string? keep, CancellationToken ct);

    /// <summary>
    /// Stops the chat's running turn at its next tool step, keeping the chat. Returns what came of it: stopped, stopping
    /// at its next step (it writes, or is in a long step), or done before the stop came.
    /// </summary>
    Task<string> StopChatAsync(YardChat chat, CancellationToken ct);

    /// <summary>Changes the defaults; a null leaves that one as it is.</summary>
    Task<ChatDefaults> SetDefaultsAsync(string? model, string? effort, CancellationToken ct);

    /// <summary>Shows the workspace in the Cab, with the chat's tab in front in its VS Code when one is given.</summary>
    Task<string> OpenWorkspaceAsync(YardWorkspace workspace, YardChat? chat, CancellationToken ct);

    /// <summary>Shows the Yard.</summary>
    Task BackToYardAsync(CancellationToken ct);

    /// <summary>
    /// Minimizes, maximizes or restores CodeSwitchX's own window (#117); maximize and restore also bring it to the front.
    /// Returns what Raven says: done, already so, or that Windows kept another window in front.
    /// </summary>
    Task<string> SetWindowAsync(WindowRequest request, CancellationToken ct);

    /// <summary>
    /// Shows another chat in Raven's panel (and its window in the Cab when it says open); returns what Raven says of it,
    /// "Chat 3, ContentAutomatorX.". Throws when no chat has the number.
    /// </summary>
    Task<string> SwitchChatAsync(ChatSwitch target, CancellationToken ct);

    /// <summary>
    /// Mutes or unmutes a window's Raven chat (#153): its news and its catch-up only written, with no sound; its questions still
    /// read out. Returns what Raven says of it; throws for chat 0 and a number no window has.
    /// </summary>
    Task<string> MuteChatAsync(int number, bool muted, CancellationToken ct);

    /// <summary>
    /// Takes the user to the oldest question or permission prompt waiting in any window (#230): its window's Raven chat is
    /// shown, and its card is read out once Raven's answer is over. Returns what Raven says of it.
    /// </summary>
    /// <param name="askedIn">The key of the Raven chat whose brain asks, as its chat header names it; null for a caller that is
    /// no Raven chat. Muted, the card is read out only when that brain answers words said aloud (#254).</param>
    Task<string> NextQuestionAsync(string? askedIn, CancellationToken ct);

    /// <summary>
    /// What is new for the user (#243): the news not read yet in every window's Raven chat, muted ones too, the chat
    /// <paramref name="askedIn"/> names first (chat 0's for none), or only chat <paramref name="number"/>'s; and what waits
    /// for the user. Facts only, never what a chat said. What it gives counts as read once the answer of the brain that
    /// asked is heard, or at once when that is only written.
    /// </summary>
    /// <param name="askedIn">The key of the Raven chat whose brain asks, as its chat header names it; null for a caller that
    /// is no Raven chat, whose answer nothing counts as read by (#254).</param>
    Task<string> WhatsNewAsync(string? askedIn, int? number, CancellationToken ct);

    /// <summary>
    /// Sums the chat up from its conversation (#234): what it was asked, what it did, where it stands, what it waits for. The
    /// whole summary is written in the Raven chat <paramref name="askedIn"/> names (the one the user is in for null); returns
    /// the short part, which Raven says.
    /// </summary>
    /// <exception cref="YardActionException">It could not be summed up; the message says why.</exception>
    Task<string> SummarizeChatAsync(YardChat chat, string? askedIn, CancellationToken ct);

    /// <summary>
    /// Sums the user's last working session up (#237): when it was, what each chat got done, what is still open. Written
    /// in the Raven chat <paramref name="askedIn"/> names (the one the user is in for null); returns what Raven says. One
    /// that takes longer than a minute goes on, and is only written when done.
    /// </summary>
    /// <exception cref="YardActionException">There is none to sum up, or it could not be; the message says why.</exception>
    Task<string> RecapLastSessionAsync(string? askedIn, CancellationToken ct);

    /// <summary>Whether Raven started the chat while this app runs. Any thread.</summary>
    bool StartedByRaven(string chatId);
}

/// <summary>What the user asks of CodeSwitchX's own window.</summary>
public enum WindowRequest
{
    Minimize,
    Maximize,
    Restore,

    /// <summary>To the front as it is, maximized too, out of the minimized state: never a change of size (#222).</summary>
    Front,
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
