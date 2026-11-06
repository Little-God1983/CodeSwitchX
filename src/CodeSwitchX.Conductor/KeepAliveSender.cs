using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Yard;
using LittleGod.Llm.Abstractions;
using LittleGod.Llm.ClaudeCli;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Conductor;

/// <summary>
/// Sends a chat its keep-alive ping (#386) the only way a chat in a VS Code tab is reached: a message from another Claude
/// session (<c>SendMessage</c>, as Raven's brain tells chats things). The session is a small headless Claude Code started
/// for each ping, so it works with Raven's panel and voice off: the cheapest model, a prompt of two lines, the send tool
/// alone, no hooks (it would show as a chat on the Yard) and nothing saved. It runs in a folder of its own, whose name the
/// chat shows the sender by ("keepwarm-3f").
/// </summary>
/// <param name="claude">The package's <see cref="ClaudeCliChatClient"/>, made with <see cref="Options"/>.</param>
/// <param name="folder">The folder it runs in; made when missing.</param>
public sealed class KeepAliveSender(IChatClient claude, string folder, ILogger<KeepAliveSender> logger) : IKeepAliveSender
{
    /// <summary>How long a send may take: Claude Code starts in a few seconds, and the send itself is one step.</summary>
    internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(90);

    private const string SystemPrompt = "You pass one message on for the CodeSwitchX app. Do exactly what the request says and nothing else: "
        + "no other tools, no questions, no comments.";

    public static ClaudeCliOptions Options() => new()
    {
        Isolation = ClaudeIsolation.UserSettingsNoHooks,
        DefaultSystemPrompt = SystemPrompt,
        DefaultModel = "claude-haiku-5-5",
        KeepApiKeyEnvironment = true,
        PersistSessions = false,
        WallClockTimeout = Limit,
    };

    public async Task<bool> SendAsync(string sendName, CancellationToken ct)
    {
        string answer;
        try
        {
            Directory.CreateDirectory(folder);
            var options = new ChatOptions().WithClaudeCli(c =>
            {
                c.Tools = [ClaudeCliBrain.SendTool];
                c.AllowedTools = [ClaudeCliBrain.SendTool];
                c.WorkingDirectory = folder;
            });
            var response = await claude.GetResponseAsync([new ChatMessage(ChatRole.User, Request(sendName))], options, ct).ConfigureAwait(false);
            answer = response.Text.Trim();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is LlmException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The keep-alive ping to {Name} could not be sent", sendName);
            return false;
        }

        if (answer.StartsWith("sent", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        logger.LogWarning("The keep-alive ping to {Name} was not sent: {Answer}", sendName, answer.Length > 0 ? answer : "the sender said nothing");
        return false;
    }

    /// <summary>What the sender is asked: the chat's name and the message, and one word back.</summary>
    internal static string Request(string sendName) =>
        $"Send one message with the {ClaudeCliBrain.SendTool} tool to the Claude session named \"{sendName}\". The message, word for word:\n\n"
        + KeepAlive.Text
        + "\n\nSend it once, to that session only. Then answer \"sent\" if the tool took it, or \"failed: \" and what the tool said.";
}
