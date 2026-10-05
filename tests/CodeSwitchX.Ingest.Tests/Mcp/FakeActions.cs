using CodeSwitchX.Core.Yard;

namespace CodeSwitchX.Ingest.Tests.Mcp;

/// <summary>Records what the tools ask for; <see cref="Refusal"/> makes every action refuse with that message.</summary>
internal sealed class FakeActions : IYardActions
{
    public List<string> Calls { get; } = [];

    public string? Refusal { get; set; }

    /// <summary>What every action throws instead, when set: the app failed, it did not refuse.</summary>
    public Exception? Failure { get; set; }

    public (YardWorkspace Workspace, YardFolder? Folder, string? Model, string? Effort)? Started { get; private set; }

    public YardWorkspace? Opened { get; private set; }

    /// <summary>The chat the last open was to show in front; null for the workspace alone.</summary>
    public YardChat? OpenedChat { get; private set; }

    public HashSet<string> Voice { get; } = [];

    public ChatDefaults Defaults { get; private set; } = new(null, null);

    public bool StartedByRaven(string chatId) => Voice.Contains(chatId);

    public Task<VoiceChatView> StartChatAsync(YardWorkspace workspace, YardFolder? folder, string? model, string? effort, CancellationToken ct)
    {
        Act("start_chat");
        Started = (workspace, folder, model, effort);
        return Task.FromResult(new VoiceChatView("dddddddd-0004", workspace.Id, workspace.Name, folder?.Path ?? workspace.RootPath,
            "claude-fable-5-1", "high", "diffusionnexus-4f"));
    }

    public YardChat? Closed { get; private set; }

    public Task<string> CloseChatAsync(YardChat chat, CancellationToken ct)
    {
        Act("close_chat");
        Closed = chat;
        return Task.FromResult("closed");
    }

    public YardChat? Stopped { get; private set; }

    public Task<string> StopChatAsync(YardChat chat, CancellationToken ct)
    {
        Act("stop_chat");
        Stopped = chat;
        return Task.FromResult("stopped");
    }

    public Task<ChatDefaults> SetDefaultsAsync(string? model, string? effort, CancellationToken ct)
    {
        Act($"set_defaults {model} {effort}");
        Defaults = new ChatDefaults(model ?? Defaults.Model, effort ?? Defaults.Effort);
        return Task.FromResult(Defaults);
    }

    public ChatSwitch? Switched { get; private set; }

    public (int Number, bool Muted)? MuteSet { get; private set; }

    public Task<string> MuteChatAsync(int number, bool muted, CancellationToken ct)
    {
        Act("mute_chat");
        MuteSet = (number, muted);
        return Task.FromResult($"Chat {number} muted: {muted}.");
    }

    public Task<string> SwitchChatAsync(ChatSwitch target, CancellationToken ct)
    {
        Act("switch_chat");
        Switched = target;
        return Task.FromResult($"Chat {target.Number}.");
    }

    public Task<string> OpenWorkspaceAsync(YardWorkspace workspace, YardChat? chat, CancellationToken ct)
    {
        Act("open_workspace");
        Opened = workspace;
        OpenedChat = chat;
        return Task.FromResult("open");
    }

    public Task BackToYardAsync(CancellationToken ct)
    {
        Act("back_to_yard");
        return Task.CompletedTask;
    }

    public WindowRequest? Window { get; private set; }

    public Task<string> SetWindowAsync(WindowRequest request, CancellationToken ct)
    {
        Act("set_window");
        Window = request;
        return Task.FromResult($"CodeSwitchX: {request}.");
    }

    private void Act(string call)
    {
        Calls.Add(call);
        if (Failure is { } failure)
        {
            throw failure;
        }

        if (Refusal is { } refusal)
        {
            throw new YardActionException(refusal);
        }
    }
}
