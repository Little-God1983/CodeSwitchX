using System.Collections.Concurrent;
using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
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

    /// <summary>
    /// Shows the chat in Raven's panel, its window in the Cab too when it says open; what Raven says of it and its
    /// workspace (none for the Yard and Activity), or null when no chat has the number.
    /// </summary>
    (string Said, Guid? WorkspaceId)? SwitchChat(ChatSwitch target);

    /// <summary>Shows, stores and uses the defaults, as if they were picked in Settings.</summary>
    void SetChatDefaults(ChatDefaults defaults);

    /// <summary>Marks a chat's row as one Raven started, with the model and effort it started with.</summary>
    void MarkVoice(string sessionId, string? label);

    /// <summary>Takes a chat that was closed on purpose off its tile at once; it shows again only if it is opened again.</summary>
    void ForgetChat(string sessionId);

    /// <summary>Minimizes, maximizes or restores the window, as its title bar buttons do; returns what Raven says of it.</summary>
    string SetWindow(WindowRequest request);

    /// <summary>Mutes or unmutes a window's Raven chat (#153); what Raven says of it, or null when no window's chat has the number.</summary>
    string? MuteChat(int number, bool muted);

    /// <summary>
    /// Raven, asked in the chat <paramref name="askedIn"/> names, started a chat in <paramref name="workspaceId"/>'s window
    /// (#180): the user is moved to that window's Raven chat, with the question and its answer.
    /// </summary>
    void FollowWork(string askedIn, Guid workspaceId);
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

    /// <summary>How long a stop is waited for before Raven says it lands at the chat's next step: a step takes seconds.</summary>
    internal static readonly TimeSpan StopWait = TimeSpan.FromSeconds(10);

    private readonly IVsCodeChats _vsCode;
    private readonly ChatSettings _chats;
    private readonly Action<string, Guid> _claim;
    private readonly Func<IRavenShell> _shell;
    private readonly IUiDispatcher _ui;
    private readonly Func<Guid, CancellationToken, Task<Workspace?>> _workspaceOf;
    private readonly TurnStops _stops;
    private readonly TimeProvider _time;
    private readonly ILogger<RavenActions> _logger;
    private readonly ConcurrentDictionary<string, bool> _started = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The chats being compacted: their tab closes and opens again, which is no end of the chat (#226).</summary>
    private readonly ConcurrentDictionary<string, bool> _compacting = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="bus">Where the chats' changes come: one that ends is Raven's no more.</param>
    /// <param name="claim">Puts a chat on a tile before its first event (<c>SessionEngine.Claim</c>).</param>
    /// <param name="shell">The window; asked for when first needed, since it is made after the services that call this.</param>
    /// <param name="workspaceOf">The registered workspace with this id; null when it is gone.</param>
    /// <param name="stops">Where a stop for a chat's running turn is asked for, which its hook takes at its next step.</param>
    public RavenActions(IVsCodeChats vsCode, ChatSettings chats, IEventBus bus, Action<string, Guid> claim, Func<IRavenShell> shell, IUiDispatcher ui,
        Func<Guid, CancellationToken, Task<Workspace?>> workspaceOf, TurnStops stops, TimeProvider time, ILogger<RavenActions> logger)
    {
        _vsCode = vsCode;
        _chats = chats;
        _claim = claim;
        _shell = shell;
        _ui = ui;
        _workspaceOf = workspaceOf;
        _stops = stops;
        _time = time;
        _logger = logger;
        // Lives as long as the app: the subscription is never ended.
        bus.Subscribe<SessionChanged>(Ended);
    }

    /// <summary>A chat Raven started that has ended (its tab closed, its process gone) loses its mark and is Raven's no more.</summary>
    private void Ended(SessionChanged change)
    {
        var id = change.Current.SessionId;
        if (!SessionStateMachine.IsLive(change.Current.State) && !_compacting.ContainsKey(id) && _started.TryRemove(id, out _))
        {
            _ui.Post(() => _shell().MarkVoice(id, null));
        }
    }

    public ChatDefaults Defaults => _chats.Defaults;

    public bool StartedByRaven(string chatId) => _started.ContainsKey(chatId);

    public async Task<VoiceChatView> StartChatAsync(YardWorkspace workspace, YardFolder? folder, string? model, string? effort, string? askedIn,
        CancellationToken ct)
    {
        var modelId = ChatSettings.Blank(model) is { } m ? _chats.ModelIdOf(m) : _chats.DefaultModelId;
        var level = ChatSettings.Blank(effort) is { } e ? ChatSettings.EffortOf(e) : _chats.Defaults.Effort;
        var registered = await _workspaceOf(workspace.Id, ct).ConfigureAwait(false)
            ?? throw new YardActionException($"{workspace.Name} is not on the Yard any more.");

        var chat = await _vsCode.StartAsync(registered, folder?.Path, modelId, level, ct).ConfigureAwait(false);
        _claim(chat.SessionId, workspace.Id);
        _started[chat.SessionId] = true;
        _ui.Post(() => _shell().MarkVoice(chat.SessionId, Label(modelId, level)));
        if (askedIn is not null)
        {
            // The user follows the work to the window's chat, where the chat's news comes (#180); the panel knows whether
            // that is where they are.
            _ui.Post(() => _shell().FollowWork(askedIn, workspace.Id));
        }

        _logger.LogInformation("Raven opened chat {Id} in {Workspace}", chat.SessionId, workspace.Name);
        return new VoiceChatView(chat.SessionId, workspace.Id, workspace.Name, chat.Folder, modelId, level, chat.SendTo);
    }

    public async Task<string> CloseChatAsync(YardChat chat, CancellationToken ct)
    {
        await _vsCode.CloseAsync(chat.Id, ct).ConfigureAwait(false);
        _started.TryRemove(chat.Id, out _);

        // Closed on purpose: no ended row lingers on the tile, and none is news. Its tab is gone either way, so a window
        // too busy to take the row off now does not make the close a failure; it takes it off with the chat's end.
        _ui.Post(() => _shell().ForgetChat(chat.Id));
        _logger.LogInformation("Raven closed chat {Id} in {Workspace}", chat.Id, chat.Workspace);
        return $"The {chat.Title} chat is closed. Its conversation stays in VS Code's session list, where the user can open it again.";
    }

    public async Task<string> CompactChatAsync(YardChat chat, string? keep, CancellationToken ct)
    {
        var folder = chat.Cwd ?? throw new YardActionException($"CodeSwitchX does not know the folder the {chat.Title} chat runs in, so it cannot compact it.");
        _logger.LogInformation("Raven compacts chat {Id} in {Workspace}", chat.Id, chat.Workspace);
        if (chat.State == SessionState.Working || chat.NeedsYou)
        {
            _stops.CutOff(chat.Id); // compacted anyway: the turn its tab's close cuts off is no news
        }

        bool reopened;
        _compacting[chat.Id] = true;
        try
        {
            reopened = await _vsCode.CompactAsync(chat.Id, folder, chat.Title, keep, ct).ConfigureAwait(false);
        }
        finally
        {
            _compacting.TryRemove(chat.Id, out _);
        }

        _logger.LogInformation("Raven compacted chat {Id} in {Workspace}", chat.Id, chat.Workspace);
        return reopened
            ? $"The {chat.Title} chat is compacted, and its tab is open again."
            : $"The {chat.Title} chat is compacted.";
    }

    public async Task<string> StopChatAsync(YardChat chat, CancellationToken ct)
    {
        if (_stops.CanStop(chat.Id) == false)
        {
            throw OldRelay();
        }

        var stopped = _stops.Request(chat.Id);
        TurnStopOutcome outcome;
        try
        {
            outcome = await stopped.WaitAsync(StopWait, _time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Still asked for: it lands at the chat's next step, whatever keeps it from one now (writing, a long step, a sub-agent).
            _logger.LogInformation("Raven asked chat {Id} in {Workspace} to stop; it stops at its next step", chat.Id, chat.Workspace);
            return $"The {chat.Title} chat stops at its next step; it has not reached one yet.";
        }

        _logger.LogInformation("Raven's stop of chat {Id} in {Workspace}: {Outcome}", chat.Id, chat.Workspace, outcome);
        return outcome switch
        {
            TurnStopOutcome.Stopped => $"The {chat.Title} chat is stopped. It keeps all it did; telling it to continue carries on.",
            TurnStopOutcome.OldRelay => throw OldRelay(),
            _ => $"The {chat.Title} chat finished its turn before the stop came.",
        };
    }

    private static YardActionException OldRelay() => new("The hooks Claude Code runs are an older CodeSwitchX's, which cannot stop a chat. "
        + "Install the hooks again in CodeSwitchX's Settings, then try again; the chat can be stopped in its VS Code tab meanwhile.");

    public async Task<ChatDefaults> SetDefaultsAsync(string? model, string? effort, CancellationToken ct)
    {
        var current = _chats.Defaults;
        var next = new ChatDefaults(
            ChatSettings.Blank(model) is { } m ? _chats.DefaultModelOf(m) : current.Model,
            ChatSettings.Blank(effort) is { } e ? ChatSettings.DefaultEffortOf(e) : current.Effort);
        await OnUiAsync(() => _shell().SetChatDefaults(next), ct).ConfigureAwait(false);
        return _chats.Defaults;
    }

    public async Task<string> OpenWorkspaceAsync(YardWorkspace workspace, YardChat? chat, CancellationToken ct)
    {
        if (await ShowInCabAsync(workspace.Id, ct).ConfigureAwait(false) is { } problem)
        {
            throw new YardActionException($"{workspace.Name} could not be opened: {problem}");
        }

        if (chat is null)
        {
            return $"{workspace.Name} is open.";
        }

        // The workspace shows either way: a chat that cannot be shown is said as that, not as the workspace failing (#115).
        try
        {
            var registered = await _workspaceOf(workspace.Id, ct).ConfigureAwait(false)
                ?? throw new YardActionException("its workspace is not on the Yard any more.");
            await _vsCode.ShowAsync(registered, chat.Id, ct).ConfigureAwait(false);
        }
        catch (YardActionException ex)
        {
            throw new YardActionException($"{workspace.Name} is open, but the chat \"{chat.Title}\" is not in front: {ex.Message}");
        }

        return $"{workspace.Name} is open, with the chat \"{chat.Title}\" in front.";
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

    public Task BackToYardAsync(CancellationToken ct) => OnUiAsync(() => _shell().ShowYard(), ct);

    public Task<string> SetWindowAsync(WindowRequest request, CancellationToken ct) =>
        _ui.InvokeAsync(() => _shell().SetWindow(request), UiTimeout, ct);

    public Task<string> MuteChatAsync(int number, bool muted, CancellationToken ct) => _ui.InvokeAsync(() => _shell().MuteChat(number, muted)
        ?? throw new YardActionException(number == 0
            ? "Chat 0 has no mute of its own: Raven's mute button quiets everything. Nothing was changed."
            : $"No window has the number {number}: list_workspaces shows each one's number. Nothing was changed."), UiTimeout, ct);

    /// <summary>The window is opened as open_workspace opens it, so a failure or a window that never shows is said.</summary>
    public async Task<string> SwitchChatAsync(ChatSwitch target, CancellationToken ct)
    {
        (string Said, Guid? WorkspaceId)? switched = null;
        await OnUiAsync(() => switched = _shell().SwitchChat(target with { Open = false }), ct).ConfigureAwait(false);
        if (switched is not { } done)
        {
            throw new YardActionException($"No window has the number {target.Number}: list_workspaces shows each one's number.");
        }

        if (target.Open && done.WorkspaceId is { } workspace && await ShowInCabAsync(workspace, ct).ConfigureAwait(false) is { } problem)
        {
            throw new YardActionException($"{done.Said} Its window could not be opened: {problem}");
        }

        return done.Said;
    }

    /// <summary>How the row's voice mark reads: "Fable 5.1 · high".</summary>
    internal static string Label(string? model, string? effort) =>
        $"{(model is null ? "default model" : ChatModels.DisplayName(model))} · {effort ?? "default effort"}";

    private Task OnUiAsync(Action action, CancellationToken ct) => _ui.InvokeAsync(() =>
    {
        action();
        return true;
    }, UiTimeout, ct);
}
