using System.ComponentModel;
using CodeSwitchX.Core.Yard;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CodeSwitchX.Ingest.Mcp;

/// <summary>
/// What Raven's brain can do on the Yard, as MCP tools: start a Claude chat by voice and steer or stop it, set the model
/// and effort chats start with, and move between the Yard and a workspace. Names are matched here, like the looking tools
/// match them; what cannot be done comes back as a tool error in words the brain can repeat.
/// </summary>
[McpServerToolType]
public sealed class YardActionTools(IYardDirectory yard, IYardActions actions)
{
    [McpServerTool(Name = "start_chat", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Starts a new Claude Code chat in a workspace, with the prompt as its first message; it shows on the workspace's tile. "
        + "It runs in the folder the workspace name matched (the matched folder of a multi-folder workspace, else the root), or in the folder "
        + "named. Leave model and effort out to use the defaults; give them only when the user wants them for this one chat.")]
    public async Task<StartedChatView> StartChat(
        [Description("The workspace or project as the user named it, matched like find_workspace.")] string workspace,
        [Description("What the chat is to do, as the user asked for it, without the parts that only say where and with what it runs.")] string prompt,
        [Description("A folder of the workspace to run in, when the user named one apart from the workspace.")] string? folder = null,
        [Description("A model for this chat only: an alias (Fable, Opus, Sonnet, Haiku) or a full id.")] string? model = null,
        [Description("An effort level for this chat only: low, medium, high, xhigh or max.")] string? effort = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new McpException("Say what the chat is to do: the prompt is empty.");
        }

        var match = await OneWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);
        var where = WorkspaceMatcher.FolderOf(match);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            where = WorkspaceMatcher.FindFolder(folder, match.Workspace)
                ?? throw new McpException($"{match.Workspace.Name} has no folder like '{folder}'. Its folders: {string.Join(", ", match.Workspace.Folders.Select(f => f.Name))}.");
        }

        var started = await Act(() => actions.StartChatAsync(match.Workspace, where, prompt.Trim(), model, effort, cancellationToken)).ConfigureAwait(false);
        return new StartedChatView(VoiceChatOf(started.Chat), started.Note);
    }

    [McpServerTool(Name = "send_to_chat", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Sends the user's words to a chat Raven started, as its next message. Only chats Raven started take this (started_by_raven "
        + "in list_chats); one that is working takes it once its turn is over. Every other chat is told with SendMessage, to its send_to "
        + "name from list_chats.")]
    public async Task<VoiceChatInfo> SendToChat(
        [Description("The chat's id from start_chat or list_chats; its first 8 characters are enough.")] string chat,
        [Description("What to tell the chat, as the user said it.")] string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new McpException("Say what to tell the chat: the text is empty.");
        }

        return VoiceChatOf(await Act(() => actions.SendToChatAsync(chat ?? "", text.Trim(), cancellationToken)).ConfigureAwait(false));
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
    [Description("Shows a workspace's VS Code in CodeSwitchX (\"open it\", \"take me to …\"). Given a chat Raven started, that chat is handed "
        + "over to VS Code: Raven stops running it, and VS Code opens the same conversation for the user to go on by hand.")]
    public async Task<string> OpenWorkspace(
        [Description("The workspace as the user named it; may be left out when a chat is given.")] string? workspace = null,
        [Description("A chat Raven started, to open in VS Code; its id from start_chat or list_chats.")] string? chat = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspace) && string.IsNullOrWhiteSpace(chat))
        {
            throw new McpException("Say which workspace or chat to open.");
        }

        var target = string.IsNullOrWhiteSpace(workspace) ? null : (await OneWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false)).Workspace;
        var key = string.IsNullOrWhiteSpace(chat) ? null : chat.Trim();
        if (key is not null && !actions.VoiceChats.Any(v => v.Id.StartsWith(key, StringComparison.OrdinalIgnoreCase)))
        {
            // A chat VS Code runs: it is already there, in its workspace, which is what opens.
            var found = (await yard.ChatsAsync(cancellationToken).ConfigureAwait(false))
                .Where(c => c.Id.StartsWith(key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (target is null && found is [var one])
            {
                target = (await yard.WorkspacesAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(w => w.Id == one.WorkspaceId);
            }

            if (target is null)
            {
                throw new McpException($"The Yard shows no chat '{chat}'. Name its workspace to open that instead.");
            }

            key = null;
        }

        return await Act(() => actions.OpenWorkspaceAsync(target, key, cancellationToken)).ConfigureAwait(false);
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

    [McpServerTool(Name = "stop_chat", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Stops a chat Raven started; what it is doing is cut off. Never call it before you have asked the user \"Stop the … chat?\" "
        + "and they said yes in their next words. Only chats Raven started can be stopped here.")]
    public Task<string> StopChat(
        [Description("The chat's id from start_chat or list_chats.")] string chat,
        CancellationToken cancellationToken = default) =>
        Act(() => actions.StopChatAsync(chat ?? "", cancellationToken));

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

    private static VoiceChatInfo VoiceChatOf(VoiceChatView chat) => new(chat.Id, chat.Workspace, Path.GetFileName(Path.TrimEndingDirectorySeparator(chat.Folder)),
        chat.Model ?? "Claude Code's default", chat.Effort ?? "Claude Code's default", chat.Working ? "working" : "idle, its turn is over",
        chat.PermissionMode);
}

/// <param name="Note">Something to tell the user about how it runs, or null.</param>
public sealed record StartedChatView(VoiceChatInfo Chat, string? Note);

/// <param name="Folder">The name of the folder it runs in.</param>
/// <param name="PermissionMode">How Claude Code runs it ("auto"); null until it said.</param>
public sealed record VoiceChatInfo(string Id, string Workspace, string Folder, string Model, string Effort, string State, string? PermissionMode);
