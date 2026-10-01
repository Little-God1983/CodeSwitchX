using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Infrastructure;
using Microsoft.Extensions.Logging;
using Path = System.IO.Path;

namespace CodeSwitchX.UI.Raven;

/// <summary>What Raven's actions do to the window; the shell does it, on the UI thread.</summary>
public interface IRavenShell
{
    /// <summary>Brings the window forward and shows the workspace in the Cab; null once it is shown, else why not.</summary>
    Task<string?> OpenInCabAsync(Guid workspaceId);

    void ShowYard();

    /// <summary>Whether the workspace's VS Code window is the one the user works in now. Any thread.</summary>
    bool IsVsCodeInFront(Guid workspaceId);

    /// <summary>Shows, stores and uses the defaults, as if they were picked in Settings.</summary>
    void SetChatDefaults(ChatDefaults defaults);

    /// <summary>Marks a chat's row as one the app runs for Raven, with how it runs; null takes the mark off.</summary>
    void MarkVoice(string sessionId, string? label);

    /// <summary>A warning in Raven's log.</summary>
    void Warn(string text);
}

/// <summary>
/// What Raven's brain does on the Yard (<see cref="IYardActions"/>), done by the app: chats started through the
/// <see cref="IAgentLauncher"/> and placed on the tile they were started for, the defaults changed as in Settings, the
/// Cab and the Yard shown by the shell. A chat taken over by VS Code is stopped here first, then opened there by its
/// <c>vscode://</c> link, so only one process writes to it. It also keeps the Yard's voice marks and Raven's log up to
/// date with the chats it runs.
/// </summary>
public sealed class RavenActions : IYardActions
{
    /// <summary>How long a call waits for the UI thread: a tool call must fail, not hang, when the window is stuck.</summary>
    internal static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long opening a workspace may take: VS Code started from cold takes a while to show its window.</summary>
    internal static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(90);

    private readonly IAgentLauncher _agents;
    private readonly ChatSettings _chats;
    private readonly Action<string, Guid> _claim;
    private readonly Func<IRavenShell> _shell;
    private readonly IUiDispatcher _ui;
    private readonly Func<string, string?> _openUrl;
    private readonly TimeProvider _time;
    private readonly ILogger<RavenActions> _logger;

    /// <param name="claim">Puts a chat on a tile before its first event (<c>SessionEngine.Claim</c>).</param>
    /// <param name="shell">The window; asked for when first needed, since it is made after the services that call this.</param>
    /// <param name="openUrl">Hands a <c>vscode://</c> link to VS Code; null once handed over, else why not.</param>
    public RavenActions(IAgentLauncher agents, ChatSettings chats, Action<string, Guid> claim, Func<IRavenShell> shell, IUiDispatcher ui,
        Func<string, string?> openUrl, TimeProvider time, ILogger<RavenActions> logger)
    {
        _agents = agents;
        _chats = chats;
        _claim = claim;
        _shell = shell;
        _ui = ui;
        _openUrl = openUrl;
        _time = time;
        _logger = logger;
        _agents.Changed += chat => _ui.Post(() => _shell().MarkVoice(chat.Id, chat.Ended ? null : Label(chat.Model, chat.Effort)));
        _agents.Failed += (chat, why) => _ui.Post(() => _shell().Warn($"Raven's chat in {chat.Workspace} ({FolderName(chat.Folder)}): {why}"));
    }

    /// <summary>How often a hand-over looks whether the workspace's VS Code is in front yet.</summary>
    internal static readonly TimeSpan FrontPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>How long "open it" waits for VS Code to come to the front before it answers that the chat opens later.</summary>
    internal static readonly TimeSpan FrontWait = TimeSpan.FromSeconds(3);

    /// <summary>How long a hand-over keeps waiting for the user to come to VS Code, after "open it" has answered.</summary>
    internal static readonly TimeSpan PendingHandOver = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a hand-over waits once VS Code is in front before it sends the link, so VS Code has taken the window as
    /// its most recently active one.
    /// </summary>
    internal TimeSpan HandOverDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The hand-over still waiting for VS Code to come to the front, if any; completes once it is done or given up.</summary>
    internal Task PendingHandOverTask { get; private set; } = Task.CompletedTask;

    public ChatDefaults Defaults => _chats.Defaults;

    public IReadOnlyList<VoiceChatView> VoiceChats => _agents.Chats.Select(View).ToList();

    public async Task<StartedChat> StartChatAsync(YardWorkspace workspace, YardFolder folder, string prompt, string? model, string? effort, CancellationToken ct)
    {
        var modelId = ChatSettings.Blank(model) is { } m ? ModelIdOf(m) : _chats.DefaultModelId;
        var level = ChatSettings.Blank(effort) is { } e ? EffortOf(e) : _chats.Defaults.Effort;
        var id = Guid.NewGuid().ToString();
        _claim(id, workspace.Id);
        var start = await _agents.StartAsync(new AgentRequest(id, workspace.Id, workspace.Name, folder.Path, prompt, modelId, level), ct).ConfigureAwait(false);
        if (start.Failure is { } failure)
        {
            throw new YardActionException(failure);
        }

        // Haiku 4.5 cannot run in auto mode: Claude Code runs it in default mode, and in -p nothing can approve a request,
        // so each edit or command the user's settings do not allow is refused, and the turn goes on without it.
        var note = start.Chat.PermissionMode is { } mode && mode != "auto"
            ? $"{(modelId is null ? "This model" : ChatModels.DisplayName(modelId))} cannot run in auto mode, so the chat runs in {mode} mode: "
                + "each edit or command the user's settings do not allow is refused, as nothing can approve it. For changes, start it with another model."
            : null;
        return new StartedChat(View(start.Chat), note);
    }

    public async Task<VoiceChatView> SendToChatAsync(string chatId, string text, CancellationToken ct) =>
        View(await _agents.SendAsync(chatId, text, ct).ConfigureAwait(false));

    public async Task<ChatDefaults> SetDefaultsAsync(string? model, string? effort, CancellationToken ct)
    {
        var current = _chats.Defaults;
        var next = new ChatDefaults(
            ChatSettings.Blank(model) is { } m ? NameOf(m) : current.Model,
            ChatSettings.Blank(effort) is { } e ? EffortOf(e) : current.Effort);
        await OnUiAsync(() => _shell().SetChatDefaults(next), ct).ConfigureAwait(false);
        return _chats.Defaults;
    }

    public async Task<string> OpenWorkspaceAsync(YardWorkspace? workspace, string? chatId, CancellationToken ct)
    {
        var chat = chatId is null ? null : _agents.Find(chatId);
        if (chat is null && workspace is null)
        {
            throw new YardActionException($"No chat Raven started has the id '{chatId}'. Name its workspace to open that instead.");
        }

        // The chat's own tile wins: "open it" is about where it runs.
        var workspaceId = chat?.WorkspaceId ?? workspace!.Id;
        var name = chat?.Workspace ?? workspace!.Name;

        // The workspace first: when it cannot be shown, Raven's chat goes on as it was, and nothing is lost.
        if (await ShowInCabAsync(workspaceId, ct).ConfigureAwait(false) is { } problem)
        {
            throw new YardActionException($"{name} could not be opened: {problem}" + (chat is null ? "" : " The chat goes on running here."));
        }

        if (chat is null)
        {
            return $"{name} is open.";
        }

        // Only one process may write to the chat: Raven's stops before VS Code takes it over. From here on the hand-over
        // is seen through even when the brain's call is cancelled, or the chat would run neither here nor there. Each step
        // is bounded: the stop by its waits, the look for the front by FrontWait.
        bool cutOff;
        try
        {
            cutOff = (await _agents.StopAsync(chat.Id, CancellationToken.None).ConfigureAwait(false)).Working;
        }
        catch (YardActionException)
        {
            cutOff = false; // it ended by itself in the meantime
        }

        // VS Code gives the link to the window focused last. When the user is in another app, CodeSwitchX cannot take
        // the front, and the link would open the chat in whichever VS Code window they used last: it waits for them.
        var goOn = cutOff ? " It was still working, and that turn was cut off; its input box says to go on." : "";
        if (!await InFrontWithinAsync(workspaceId, FrontWait, CancellationToken.None).ConfigureAwait(false))
        {
            PendingHandOverTask = HandOverWhenInFrontAsync(chat, workspaceId, cutOff);
            return $"{name} is open in CodeSwitchX, which is not in front: the chat opens in VS Code as soon as the user switches to it.{goOn}";
        }

        if (await HandOverAsync(chat.Id, cutOff, CancellationToken.None).ConfigureAwait(false) is { } failed)
        {
            throw new YardActionException($"{name} is open, but VS Code could not be asked to open the chat: {failed} It can be resumed in its Claude Code panel.");
        }

        return $"{name} is open, and the chat opens in VS Code's Claude Code panel.{goOn}";
    }

    /// <summary>Shows the workspace in the Cab; null once it is shown, else why not. Never throws for a window that is slow.</summary>
    private async Task<string?> ShowInCabAsync(Guid workspaceId, CancellationToken ct)
    {
        Task<string?> shown;
        try
        {
            shown = await _ui.InvokeAsync(() => _shell().OpenInCabAsync(workspaceId), UiTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return "CodeSwitchX's window did not respond.";
        }

        try
        {
            return await shown.WaitAsync(OpenTimeout, _time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return $"VS Code did not show its window within {OpenTimeout.TotalSeconds:0} seconds.";
        }
    }

    /// <summary>True once the workspace's VS Code is in front, looked at every <see cref="FrontPoll"/>; false when it was not within the time.</summary>
    private async Task<bool> InFrontWithinAsync(Guid workspaceId, TimeSpan within, CancellationToken ct)
    {
        var until = _time.GetUtcNow() + within;
        while (!_shell().IsVsCodeInFront(workspaceId))
        {
            if (_time.GetUtcNow() >= until)
            {
                return false;
            }

            await Task.Delay(FrontPoll, _time, ct).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Sends the link once VS Code is in front; says in Raven's log when it gave up or could not send it. Never faults.</summary>
    private async Task HandOverWhenInFrontAsync(AgentChat chat, Guid workspaceId, bool cutOff)
    {
        try
        {
            if (!await InFrontWithinAsync(workspaceId, PendingHandOver, CancellationToken.None).ConfigureAwait(false))
            {
                _ui.Post(() => _shell().Warn(
                    $"The chat Raven handed over in {chat.Workspace} was not opened in VS Code: it never came to the front. Open it from the Claude Code panel's session history."));
                return;
            }

            if (await HandOverAsync(chat.Id, cutOff, CancellationToken.None).ConfigureAwait(false) is { } failed)
            {
                _ui.Post(() => _shell().Warn($"VS Code could not be asked to open the chat Raven handed over in {chat.Workspace}: {failed}"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Handing chat {Id} over to VS Code failed", chat.Id);
        }
    }

    /// <summary>Sends the chat's link to VS Code, which is in front; null once sent, else why not.</summary>
    private async Task<string?> HandOverAsync(string chatId, bool cutOff, CancellationToken ct)
    {
        await Task.Delay(HandOverDelay, _time, ct).ConfigureAwait(false);
        var failed = _openUrl(ChatUrl(chatId, cutOff));
        if (failed is not null)
        {
            _logger.LogWarning("Could not open chat {Id} in VS Code: {Problem}", chatId, failed);
        }

        return failed;
    }

    public Task BackToYardAsync(CancellationToken ct) => OnUiAsync(() => _shell().ShowYard(), ct);

    public async Task<string> StopChatAsync(string chatId, CancellationToken ct)
    {
        var before = await _agents.StopAsync(chatId, ct).ConfigureAwait(false);
        return $"The chat in {before.Workspace} is stopped." + (before.Working ? " It was in the middle of its work." : "");
    }

    /// <summary>
    /// The link that opens a session in VS Code's Claude Code panel. A chat cut off mid-turn gets "go on" typed into its
    /// input, not sent: the link only fills the box, and the user decides.
    /// </summary>
    internal static string ChatUrl(string chatId, bool cutOff) =>
        $"vscode://anthropic.claude-code/open?session={Uri.EscapeDataString(chatId)}"
        + (cutOff ? "&prompt=" + Uri.EscapeDataString("Go on where you stopped.") : "");

    /// <summary>How the row's voice mark reads: "Fable 5.1 · high".</summary>
    internal static string Label(string? model, string? effort) =>
        $"{(model is null ? "default model" : ChatModels.DisplayName(model))} · {effort ?? "default effort"}";

    private string ModelIdOf(string said) => ChatModels.ResolveModel(said, _chats.Aliases)
        ?? throw new YardActionException($"'{said}' is no model Raven knows. Say {string.Join(", ", _chats.Aliases.Select(a => a.Name))}, or a full model id.");

    /// <summary>
    /// The alias name when the user said just that ("Opus"), so a new id for it in the table applies; else the id. A
    /// version or id said ("Opus 5.5") names that one model, which a later change of the table must not swap.
    /// </summary>
    private string NameOf(string said) => ChatModels.AliasNamed(said, _chats.Aliases)?.Name ?? ModelIdOf(said);

    private static string EffortOf(string said) => ChatModels.ResolveEffort(said)
        ?? throw new YardActionException($"'{said}' is no effort level. Say {string.Join(", ", ChatModels.EffortLevels)}.");

    private Task OnUiAsync(Action action, CancellationToken ct) => _ui.InvokeAsync(() =>
    {
        action();
        return true;
    }, UiTimeout, ct);

    private static VoiceChatView View(AgentChat chat) =>
        new(chat.Id, chat.WorkspaceId, chat.Workspace, chat.Folder, chat.Model, chat.Effort, chat.Working, chat.PermissionMode);

    private static string FolderName(string folder) => Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
}
