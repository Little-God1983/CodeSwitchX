using System.Collections.Concurrent;
using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Infrastructure;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Raven;

/// <summary>What Raven's actions do to the window; the shell does it, on the UI thread.</summary>
public interface IRavenShell
{
    /// <summary>Brings the window forward and shows the workspace in the Cab; null once it is shown, else why not.</summary>
    Task<string?> OpenInCabAsync(Guid workspaceId);

    void ShowYard();

    /// <summary>Shows, stores and uses the defaults, as if they were picked in Settings.</summary>
    void SetChatDefaults(ChatDefaults defaults);

    /// <summary>Marks a chat's row as one Raven started, with the model and effort it started with.</summary>
    void MarkVoice(string sessionId, string? label);
}

/// <summary>
/// What Raven's brain does on the Yard (<see cref="IYardActions"/>), done by the app: chats opened in the workspace's VS
/// Code (<see cref="IVsCodeChats"/>) and placed on the tile they were started for, the defaults changed as in Settings, the
/// Cab and the Yard shown by the shell. A chat Raven starts is an ordinary VS Code chat from its first second: it is told
/// things by name, like any other, and outlives this app.
/// </summary>
public sealed class RavenActions : IYardActions
{
    /// <summary>How long a call waits for the UI thread: a tool call must fail, not hang, when the window is stuck.</summary>
    internal static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long opening a workspace may take: VS Code started from cold takes a while to show its window.</summary>
    internal static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(90);

    private readonly IVsCodeChats _vsCode;
    private readonly ChatSettings _chats;
    private readonly Action<string, Guid> _claim;
    private readonly Func<IRavenShell> _shell;
    private readonly IUiDispatcher _ui;
    private readonly Func<Guid, CancellationToken, Task<Workspace?>> _workspaceOf;
    private readonly TimeProvider _time;
    private readonly ILogger<RavenActions> _logger;
    private readonly ConcurrentDictionary<string, bool> _started = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="claim">Puts a chat on a tile before its first event (<c>SessionEngine.Claim</c>).</param>
    /// <param name="shell">The window; asked for when first needed, since it is made after the services that call this.</param>
    /// <param name="workspaceOf">The registered workspace with this id; null when it is gone.</param>
    public RavenActions(IVsCodeChats vsCode, ChatSettings chats, Action<string, Guid> claim, Func<IRavenShell> shell, IUiDispatcher ui,
        Func<Guid, CancellationToken, Task<Workspace?>> workspaceOf, TimeProvider time, ILogger<RavenActions> logger)
    {
        _vsCode = vsCode;
        _chats = chats;
        _claim = claim;
        _shell = shell;
        _ui = ui;
        _workspaceOf = workspaceOf;
        _time = time;
        _logger = logger;
    }

    public ChatDefaults Defaults => _chats.Defaults;

    public bool StartedByRaven(string chatId) => _started.ContainsKey(chatId);

    public async Task<StartedChat> StartChatAsync(YardWorkspace workspace, YardFolder? folder, string? model, string? effort, CancellationToken ct)
    {
        var modelId = ChatSettings.Blank(model) is { } m ? ModelIdOf(m) : _chats.DefaultModelId;
        var level = ChatSettings.Blank(effort) is { } e ? EffortOf(e) : _chats.Defaults.Effort;
        var registered = await _workspaceOf(workspace.Id, ct).ConfigureAwait(false)
            ?? throw new YardActionException($"{workspace.Name} is not on the Yard any more.");

        var chat = await _vsCode.StartAsync(registered, folder?.Path, modelId, level, ct).ConfigureAwait(false);
        _claim(chat.SessionId, workspace.Id);
        _started[chat.SessionId] = true;
        _ui.Post(() => _shell().MarkVoice(chat.SessionId, Label(modelId, level)));
        _logger.LogInformation("Raven opened chat {Id} in {Workspace}", chat.SessionId, workspace.Name);
        return new StartedChat(new VoiceChatView(chat.SessionId, workspace.Id, workspace.Name, chat.Folder, modelId, level, chat.SendTo), null);
    }

    public async Task<ChatDefaults> SetDefaultsAsync(string? model, string? effort, CancellationToken ct)
    {
        var current = _chats.Defaults;
        var next = new ChatDefaults(
            ChatSettings.Blank(model) is { } m ? NameOf(m) : current.Model,
            ChatSettings.Blank(effort) is { } e ? EffortOf(e) : current.Effort);
        await OnUiAsync(() => _shell().SetChatDefaults(next), ct).ConfigureAwait(false);
        return _chats.Defaults;
    }

    public async Task<string> OpenWorkspaceAsync(YardWorkspace workspace, CancellationToken ct) =>
        await ShowInCabAsync(workspace.Id, ct).ConfigureAwait(false) is { } problem
            ? throw new YardActionException($"{workspace.Name} could not be opened: {problem}")
            : $"{workspace.Name} is open.";

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

    public Task BackToYardAsync(CancellationToken ct) => OnUiAsync(() => _shell().ShowYard(), ct);

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
}
