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

    /// <summary>
    /// How long a hand-over waits between showing the workspace and opening the chat: the link goes to VS Code's most
    /// recently active window, which the one just shown must have become by then.
    /// </summary>
    internal TimeSpan HandOverDelay { get; set; } = TimeSpan.FromMilliseconds(800);

    public ChatDefaults Defaults => _chats.Defaults;

    public IReadOnlyList<VoiceChatView> VoiceChats => _agents.Chats.Select(View).ToList();

    public async Task<StartedChat> StartChatAsync(YardWorkspace workspace, YardFolder folder, string prompt, string? model, string? effort, CancellationToken ct)
    {
        var modelId = model is null ? _chats.DefaultModelId : ModelIdOf(model);
        var level = effort is null ? _chats.Defaults.Effort : EffortOf(effort);
        var id = Guid.NewGuid().ToString();
        _claim(id, workspace.Id);
        var start = await _agents.StartAsync(new AgentRequest(id, workspace.Id, workspace.Name, folder.Path, prompt, modelId, level), ct).ConfigureAwait(false);
        if (start.Failure is { } failure)
        {
            throw new YardActionException(failure);
        }

        // Haiku 4.5 cannot run in auto mode: Claude Code runs it in default mode, which asks before each edit.
        var note = start.Chat.PermissionMode is { } mode && mode != "auto"
            ? $"{(modelId is null ? "This model" : ChatModels.DisplayName(modelId))} cannot run in auto mode, so the chat runs in {mode} mode: it "
                + "asks before it edits, and only VS Code can answer that. Open it to allow its edits."
            : null;
        return new StartedChat(View(start.Chat), note);
    }

    public async Task<VoiceChatView> SendToChatAsync(string chatId, string text, CancellationToken ct) =>
        View(await _agents.SendAsync(chatId, text, ct).ConfigureAwait(false));

    public async Task<ChatDefaults> SetDefaultsAsync(string? model, string? effort, CancellationToken ct)
    {
        var current = _chats.Defaults;
        var next = new ChatDefaults(
            model is null ? current.Model : NameOf(model),
            effort is null ? current.Effort : EffortOf(effort));
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
        var cutOff = false;
        if (chat is not null)
        {
            cutOff = (await _agents.StopAsync(chat.Id, ct).ConfigureAwait(false)).Working;
        }

        var shown = await _ui.InvokeAsync(() => _shell().OpenInCabAsync(workspaceId), UiTimeout, ct).ConfigureAwait(false);
        var problem = await shown.WaitAsync(OpenTimeout, _time, ct).ConfigureAwait(false);
        if (problem is not null)
        {
            throw new YardActionException(chat is null
                ? $"{name} could not be opened: {problem}"
                : $"The chat was handed over, but {name} could not be opened: {problem} It can be resumed in VS Code's Claude Code panel.");
        }

        if (chat is null)
        {
            return $"{name} is open.";
        }

        await Task.Delay(HandOverDelay, _time, ct).ConfigureAwait(false);
        if (_openUrl(ChatUrl(chat.Id, cutOff)) is { } failed)
        {
            _logger.LogWarning("Could not open chat {Id} in VS Code: {Problem}", chat.Id, failed);
            throw new YardActionException($"{name} is open, but VS Code could not be asked to open the chat: {failed} It can be resumed in its Claude Code panel.");
        }

        return $"{name} is open, and the chat opens in VS Code's Claude Code panel."
            + (cutOff ? " It was still working, and that turn was cut off; its input box says to go on." : "");
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

    /// <summary>The alias name when the model is one (so a new id for it in the table applies), else the id.</summary>
    private string NameOf(string said)
    {
        var id = ModelIdOf(said);
        return _chats.Aliases.FirstOrDefault(a => a.Id == id && ChatModels.ResolveModel(said, [a]) is not null)?.Name ?? id;
    }

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
