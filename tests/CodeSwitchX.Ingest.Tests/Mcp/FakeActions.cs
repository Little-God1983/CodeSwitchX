using CodeSwitchX.Core.Yard;

namespace CodeSwitchX.Ingest.Tests.Mcp;

/// <summary>Records what the tools ask for; <see cref="Refusal"/> makes every action refuse with that message.</summary>
internal sealed class FakeActions : IYardActions
{
    public List<string> Calls { get; } = [];

    public string? Refusal { get; set; }

    public (YardWorkspace Workspace, YardFolder Folder, string Prompt, string? Model, string? Effort)? Started { get; private set; }

    public (YardWorkspace? Workspace, string? Chat)? Opened { get; private set; }

    public List<VoiceChatView> Voice { get; } = [];

    public ChatDefaults Defaults { get; private set; } = new(null, null);

    public IReadOnlyList<VoiceChatView> VoiceChats => Voice;

    public Task<StartedChat> StartChatAsync(YardWorkspace workspace, YardFolder folder, string prompt, string? model, string? effort, CancellationToken ct)
    {
        Act("start_chat");
        Started = (workspace, folder, prompt, model, effort);
        return Task.FromResult(new StartedChat(new VoiceChatView("dddddddd-0004", workspace.Id, workspace.Name, folder.Path, "claude-fable-5-1", "high", true,
            "auto"), null));
    }

    public Task<VoiceChatView> SendToChatAsync(string chatId, string text, CancellationToken ct)
    {
        Act($"send_to_chat {chatId}: {text}");
        return Task.FromResult(new VoiceChatView(chatId, Guid.Empty, "CodeSwitchX", @"E:\Repos\CodeSwitchX", null, null, true, "auto"));
    }

    public Task<ChatDefaults> SetDefaultsAsync(string? model, string? effort, CancellationToken ct)
    {
        Act($"set_defaults {model} {effort}");
        Defaults = new ChatDefaults(model ?? Defaults.Model, effort ?? Defaults.Effort);
        return Task.FromResult(Defaults);
    }

    public Task<string> OpenWorkspaceAsync(YardWorkspace? workspace, string? chatId, CancellationToken ct)
    {
        Act("open_workspace");
        Opened = (workspace, chatId);
        return Task.FromResult("open");
    }

    public Task BackToYardAsync(CancellationToken ct)
    {
        Act("back_to_yard");
        return Task.CompletedTask;
    }

    public Task<string> StopChatAsync(string chatId, CancellationToken ct)
    {
        Act($"stop_chat {chatId}");
        return Task.FromResult("stopped");
    }

    private void Act(string call)
    {
        Calls.Add(call);
        if (Refusal is { } refusal)
        {
            throw new YardActionException(refusal);
        }
    }
}
