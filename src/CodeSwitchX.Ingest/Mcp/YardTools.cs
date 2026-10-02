using System.ComponentModel;
using System.Globalization;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Yard;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CodeSwitchX.Ingest.Mcp;

/// <summary>
/// The Yard, as MCP tools for Raven's brain. They only look: nothing here changes a workspace or a chat. What they return
/// is written for a language model to read and repeat: names and states in words, not ids and enum numbers (a chat's id
/// is there only to ask <c>get_chat</c> about it).
/// </summary>
[McpServerToolType]
public sealed class YardTools(IYardDirectory yard)
{
    /// <summary>The filters <see cref="ListChats"/> takes.</summary>
    public static readonly IReadOnlyList<string> ChatFilters = ["needs_me", "working", "live", "all"];

    [McpServerTool(Name = "list_workspaces", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the workspaces on the Yard, the user's board of VS Code workspaces: name, track (the group it is in), "
        + "folders, git state (branch and uncommitted changes per repository) and how many chats need the user or are working.")]
    public async Task<IReadOnlyList<WorkspaceView>> ListWorkspaces(CancellationToken cancellationToken)
    {
        var (workspaces, chats) = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return workspaces.Select(w => WorkspaceView.Of(w, chats)).ToList();
    }

    [McpServerTool(Name = "find_workspace", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Finds the workspaces a name means, best match first. Matches the workspace names and the names of the folders "
        + "inside them, ignoring case, spaces and punctuation, and allows for a misheard name. Use it whenever the user names a "
        + "workspace or a project: they speak, so the name reaches you through speech recognition.")]
    public async Task<IReadOnlyList<WorkspaceMatchView>> FindWorkspace(
        [Description("The name as the user said it, e.g. \"Diffusion Nexus\".")] string query, CancellationToken cancellationToken)
    {
        var (workspaces, chats) = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return WorkspaceMatcher.Find(query ?? "", workspaces)
            .Select(m => new WorkspaceMatchView(m.MatchedName, Math.Round(m.Score, 2), WorkspaceView.Of(m.Workspace, chats)))
            .ToList();
    }

    [McpServerTool(Name = "list_chats", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the Claude Code chats the Yard shows: title, workspace, state, how long it has been in it, model, last tool "
        + "and how full its context is. \"needs you\" means the chat is stopped on the user: a question to answer or something "
        + "to allow. \"idle\" means its turn is over and it waits for a new prompt; it is not in needs_me. send_to is the name "
        + "SendMessage takes to tell the chat something; a chat without one is not open in a VS Code tab (closed, or run in a "
        + "terminal) and cannot be told anything from here.")]
    public async Task<IReadOnlyList<ChatView>> ListChats(
        [Description("needs_me: waiting for the user. working: busy right now. live: every chat that has not ended. all: every chat shown.")]
        string filter = "all",
        [Description("Only the chats of the workspace this name finds best, matched like find_workspace. Leave it out for every workspace.")]
        string? workspace = null,
        CancellationToken cancellationToken = default)
    {
        // An argument sent as JSON null is bound as null, not as the default.
        filter ??= "all";
        var byName = !string.IsNullOrWhiteSpace(workspace);
        var (workspaces, chats) = byName
            ? await ReadAsync(cancellationToken).ConfigureAwait(false)
            : ([], await yard.ChatsAsync(cancellationToken).ConfigureAwait(false));
        IEnumerable<YardChat> shown = filter.Trim().ToLowerInvariant() switch
        {
            "needs_me" => chats.Where(c => c.NeedsYou),
            "working" => chats.Where(c => c.State == SessionState.Working),
            "live" => chats.Where(c => SessionStateMachine.IsLive(c.State)),
            "all" or "" => chats,
            _ => throw new McpException($"Unknown filter '{filter}'. Use one of: {string.Join(", ", ChatFilters)}."),
        };

        if (byName)
        {
            // The best match only (and its ties): the chats of a workspace that merely looks a little like the name
            // would be reported as the named workspace's.
            var ids = WorkspaceMatcher.Best(WorkspaceMatcher.Find(workspace!, workspaces)).Select(m => m.Workspace.Id).ToHashSet();
            if (ids.Count == 0)
            {
                throw new McpException($"No workspace matches '{workspace}'. list_workspaces lists them all.");
            }

            shown = shown.Where(c => ids.Contains(c.WorkspaceId));
        }

        return shown.Select(ChatView.Of).ToList();
    }

    [McpServerTool(Name = "get_chat", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One chat in detail: everything list_chats says, plus its last notification, its folder and since when it is in its state.")]
    public async Task<ChatDetailView> GetChat(
        [Description("The chat's id from list_chats; its first 8 characters are enough.")] string id, CancellationToken cancellationToken)
    {
        var key = (id ?? "").Trim();
        var chats = await yard.ChatsAsync(cancellationToken).ConfigureAwait(false);
        var found = chats.Where(c => c.Id.StartsWith(key, StringComparison.OrdinalIgnoreCase)).ToList();
        return found switch
        {
            _ when key.Length == 0 => throw new McpException("Give a chat id from list_chats."),
            [var chat] => ChatDetailView.Of(chat),
            [] => throw new McpException($"The Yard shows no chat '{id}'. list_chats lists them."),
            _ => throw new McpException($"'{id}' fits {found.Count} chats. Give more of the id."),
        };
    }

    /// <summary>The workspaces and the chats, both read at once: each read waits for the UI thread.</summary>
    private async Task<(IReadOnlyList<YardWorkspace> Workspaces, IReadOnlyList<YardChat> Chats)> ReadAsync(CancellationToken ct)
    {
        var workspaces = yard.WorkspacesAsync(ct);
        var chats = yard.ChatsAsync(ct);
        await Task.WhenAll(workspaces, chats).ConfigureAwait(false);
        return (await workspaces.ConfigureAwait(false), await chats.ConfigureAwait(false));
    }

    internal static string StateText(YardChat chat) => chat.State switch
    {
        _ when chat.NeedsYou => "needs you",
        SessionState.Working => "working",
        SessionState.Idle => "idle, its turn is over",
        SessionState.Starting => "starting",
        SessionState.Stale => "quiet for a long time",
        SessionState.Ended => "ended",
        SessionState.Errored => "failed",
        var other => other.ToString().ToLowerInvariant(),
    };

    internal static string Percent(double fill) => (Math.Clamp(fill, 0, 1) * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}

/// <param name="Git">One line per repository: "main, clean", or "DiffusionNexus: main, 3 changed" on a tile with several.</param>
public sealed record WorkspaceView(string Name, string Track, IReadOnlyList<string> Folders, IReadOnlyList<string> Git, int ChatsNeedingYou,
    int ChatsWorking, int Chats)
{
    internal static WorkspaceView Of(YardWorkspace workspace, IReadOnlyList<YardChat> chats)
    {
        var own = chats.Where(c => c.WorkspaceId == workspace.Id).ToList();
        return new WorkspaceView(
            workspace.Name,
            workspace.Track,
            workspace.Folders.Select(f => f.Name).ToList(),
            workspace.Git.Select(Line).ToList(),
            own.Count(c => c.NeedsYou),
            own.Count(c => c.State == SessionState.Working),
            own.Count);
    }

    private static string Line(YardGitLine line)
    {
        var state = line.Branch is null ? "no git" : line.Changes is null ? line.Branch : $"{line.Branch}, {line.Changes}";
        return line.Folder is null ? state : $"{line.Folder}: {state}";
    }
}

/// <param name="MatchedName">The name that matched: the workspace's, or one of its folders'.</param>
public sealed record WorkspaceMatchView(string MatchedName, double Score, WorkspaceView Workspace);

/// <param name="StartedByRaven">Raven started it (start_chat) while CodeSwitchX ran.</param>
/// <param name="SendTo">
/// The name SendMessage takes to tell this chat something; null for a chat not open in a VS Code tab (closed, or run in a
/// terminal).
/// </param>
public sealed record ChatView(string Id, string Title, string Workspace, string State, string For, string? Model, string? LastTool, string Context,
    bool StartedByRaven, string? SendTo)
{
    internal static ChatView Of(YardChat chat) => new(
        chat.Id, chat.Title, chat.Workspace, YardTools.StateText(chat), chat.StateFor, chat.Model, chat.LastTool, YardTools.Percent(chat.ContextFill),
        chat.Voice, chat.SendName);
}

/// <param name="SendTo">As <see cref="ChatView.SendTo"/>.</param>
public sealed record ChatDetailView(string Id, string Title, string Workspace, string State, string For, DateTimeOffset Since, string? Model,
    string? LastTool, string Context, string? LastNotification, string? Folder, bool StartedByRaven, string? SendTo)
{
    internal static ChatDetailView Of(YardChat chat) => new(
        chat.Id, chat.Title, chat.Workspace, YardTools.StateText(chat), chat.StateFor, chat.StateSince, chat.Model, chat.LastTool,
        YardTools.Percent(chat.ContextFill), chat.LastNotification, chat.Cwd, chat.Voice, chat.SendName);
}
