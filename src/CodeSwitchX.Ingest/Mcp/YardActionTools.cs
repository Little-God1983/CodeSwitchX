using System.ComponentModel;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Yard;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CodeSwitchX.Ingest.Mcp;

/// <summary>
/// What Raven's brain can do on the Yard, as MCP tools: open, stop and close Claude chats in a workspace's VS Code by voice, set the model
/// and effort chats start with, and move between the Yard and a workspace. Names are matched here, like the looking tools
/// match them; what cannot be done comes back as a tool error in words the brain can repeat.
/// </summary>
[McpServerToolType]
public sealed class YardActionTools(IYardDirectory yard, IYardActions actions)
{
    [McpServerTool(Name = "start_chat", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Opens a new, empty Claude Code chat in a tab of the workspace's VS Code window (VS Code is started in the background "
        + "when it does not run); it shows on the workspace's tile. VS Code starts every new chat in the workspace's first folder: a "
        + "workspace named by another of its folders, or another folder named, is refused, and you say so. It returns the chat's "
        + "send_to name: then send it its task with SendMessage to that name, or it does nothing. Leave model "
        + "and effort out to use the defaults; give them only when the user wants them for this one chat.")]
    public async Task<StartedChatView> StartChat(
        [Description("The workspace or project as the user named it, matched like find_workspace.")] string workspace,
        [Description("A folder of the workspace, only when the user named one apart from the workspace.")] string? folder = null,
        [Description("A model for this chat only: an alias (Fable, Opus, Sonnet, Haiku) or a full id.")] string? model = null,
        [Description("An effort level for this chat only: low, medium, high, xhigh or max.")] string? effort = null,
        CancellationToken cancellationToken = default)
    {
        var match = await OneWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);
        YardFolder? named;
        if (!string.IsNullOrWhiteSpace(folder))
        {
            named = WorkspaceMatcher.FindFolder(folder, match.Workspace)
                ?? throw new McpException($"{match.Workspace.Name} has no folder like '{folder}'. Its folders: {string.Join(", ", match.Workspace.Folders.Select(f => f.Name))}.");
        }
        else
        {
            // A workspace found by one of its folders' names ("Diffusion Nexus" for Diffusion-Full) is that folder asked
            // for: started elsewhere without a word, the work would land in the wrong repository.
            var matched = WorkspaceMatcher.FolderOf(match);
            named = SamePath(matched.Path, match.Workspace.RootPath) ? null : matched;
        }

        var started = await Act(() => actions.StartChatAsync(match.Workspace, named, model, effort, cancellationToken)).ConfigureAwait(false);
        return new StartedChatView(VoiceChatOf(started), $"Now send it its task: SendMessage to \"{started.SendTo}\".");
    }

    [McpServerTool(Name = "set_defaults", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Changes the model and effort every chat starts with from now on (\"let's use Opus\", \"from now on high effort\"). The panel "
        + "shows them. Leave out the one that stays. Returns the defaults now set.")]
    public Task<ChatDefaults> SetDefaults(
        [Description("An alias (Fable, Opus, Sonnet, Haiku) or a full id.")] string? model = null,
        [Description("low, medium, high, xhigh or max.")] string? effort = null,
        CancellationToken cancellationToken = default) =>
        Act(() => actions.SetDefaultsAsync(model, effort, cancellationToken));

    [McpServerTool(Name = "open_workspace", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Shows a workspace's VS Code in CodeSwitchX (\"open it\", \"take me to …\"). Given a chat, it shows the workspace the chat "
        + "runs in, where the chat is a tab of VS Code's Claude Code.")]
    public async Task<string> OpenWorkspace(
        [Description("The workspace as the user named it; may be left out when a chat is given.")] string? workspace = null,
        [Description("A chat's id from start_chat or list_chats, to show the workspace it runs in.")] string? chat = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspace) && string.IsNullOrWhiteSpace(chat))
        {
            throw new McpException("Say which workspace or chat to open.");
        }

        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var named = (await OneWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false)).Workspace;
            return await Act(() => actions.OpenWorkspaceAsync(named, cancellationToken)).ConfigureAwait(false);
        }

        var one = await OneChatAsync(chat!, " Name its workspace to open that instead.", cancellationToken).ConfigureAwait(false);
        var target = (await yard.WorkspacesAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(w => w.Id == one.WorkspaceId)
            ?? throw new McpException($"The workspace of chat '{chat}' is not on the Yard any more.");
        return await Act(() => actions.OpenWorkspaceAsync(target, cancellationToken)).ConfigureAwait(false);
    }

    [McpServerTool(Name = "back_to_yard", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Shows the Yard again, the board of all workspaces (\"back to the Yard\", \"show me everything\").")]
    public async Task<string> BackToYard(CancellationToken cancellationToken = default)
    {
        await Act(async () =>
        {
            await actions.BackToYardAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
        return "The Yard is shown.";
    }

    [McpServerTool(Name = "close_chat", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Closes a chat's tab in VS Code (\"close the … chat\"); its row leaves the tile. Any chat open in a VS Code tab can be closed, "
        + "not only ones you started. Its conversation is not deleted: it stays in VS Code's session list and can be opened again. Never call "
        + "it before you have asked the user \"Close the <title> chat?\" and they said yes in their next words. A chat that is working or "
        + "waiting for the user is refused unless anyway is true: closing it cuts its turn off, and what it is writing is lost.")]
    public async Task<string> CloseChat(
        [Description("The chat's id from list_chats or start_chat; its start is enough.")] string chat,
        [Description("True only once the user, told the chat is still working and asked \"Close it anyway?\", said yes.")] bool anyway = false,
        CancellationToken cancellationToken = default)
    {
        var one = await OneChatAsync(chat, "", cancellationToken).ConfigureAwait(false);
        if (!anyway && (one.State == SessionState.Working || one.NeedsYou))
        {
            throw new McpException($"The {one.Title} chat is {(one.NeedsYou ? "waiting for the user in the middle of its turn" : "still working")}: "
                + "closing it now cuts that turn off, and what it is writing is lost. Nothing was closed. Tell the user so and ask "
                + "\"Close it anyway?\"; only after a yes call close_chat again with anyway true.");
        }

        return await Act(() => actions.CloseChatAsync(one, cancellationToken)).ConfigureAwait(false);
    }

    [McpServerTool(Name = "stop_chat", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Stops what a working chat is doing (\"stop the … chat\"), as its stop button would, and keeps the chat with all it did. "
        + "Call it at once, without asking first. The stop lands at the chat's next tool step: one that is writing its answer or in a "
        + "long step (a test run, say) stops when that is done. It returns what came of it; say that. To carry on, the user tells the "
        + "chat to continue, through SendMessage.")]
    public async Task<string> StopChat(
        [Description("The chat's id from list_chats or start_chat; its start is enough.")] string chat,
        CancellationToken cancellationToken = default)
    {
        var one = await OneChatAsync(chat, "", cancellationToken).ConfigureAwait(false);
        if (one.NeedsYou)
        {
            throw new McpException($"The {one.Title} chat is waiting for the user, not working: they can answer or refuse it in its VS Code tab. Nothing was stopped.");
        }

        if (one.State != SessionState.Working)
        {
            throw new McpException($"The {one.Title} chat is not working on anything, so there is nothing to stop.");
        }

        return await Act(() => actions.StopChatAsync(one, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>The one chat on the Yard whose id starts so; none or more than one is an error.</summary>
    /// <param name="otherwise">Said after "no chat": what the brain can do instead.</param>
    private async Task<YardChat> OneChatAsync(string chat, string otherwise, CancellationToken ct)
    {
        var key = chat.Trim();
        var found = key.Length == 0
            ? []
            : (await yard.ChatsAsync(ct).ConfigureAwait(false)).Where(c => c.Id.StartsWith(key, StringComparison.OrdinalIgnoreCase)).ToList();
        return found switch
        {
            [var one] => one,
            [] => throw new McpException($"The Yard shows no chat '{chat}'.{otherwise}"),
            _ => throw new McpException($"'{chat}' fits more than one chat. Give more of its id."),
        };
    }

    /// <summary>The one workspace a name means; more than one equally good is a question back, none an error.</summary>
    private async Task<WorkspaceMatch> OneWorkspaceAsync(string? name, CancellationToken ct)
    {
        var workspaces = await yard.WorkspacesAsync(ct).ConfigureAwait(false);
        var best = WorkspaceMatcher.Best(WorkspaceMatcher.Find(name ?? "", workspaces));
        return best switch
        {
            [var one] => one,
            [] => throw new McpException($"No workspace matches '{name}'. list_workspaces lists them all."),
            _ => throw new McpException($"'{name}' fits {string.Join(" and ", best.Select(m => m.Workspace.Name))} equally. Ask the user which one."),
        };
    }

    private static async Task<T> Act<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (YardActionException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (TimeoutException)
        {
            // The app's window was busy past the wait: the brain gets something to tell the user, not a raw error.
            throw new McpException("CodeSwitchX's window did not respond in time, so that was not done. Say it again in a moment.");
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static VoiceChatInfo VoiceChatOf(VoiceChatView chat) => new(chat.Id, chat.Workspace, Path.GetFileName(Path.TrimEndingDirectorySeparator(chat.Folder)),
        chat.Model ?? "VS Code's default", chat.Effort ?? "VS Code's default", chat.SendTo);
}

/// <param name="Next">What the brain does next: the chat is empty until it is sent its task.</param>
public sealed record StartedChatView(VoiceChatInfo Chat, string Next);

/// <param name="Folder">The name of the folder it runs in.</param>
/// <param name="SendTo">The name SendMessage takes for it.</param>
public sealed record VoiceChatInfo(string Id, string Workspace, string Folder, string Model, string Effort, string SendTo);
