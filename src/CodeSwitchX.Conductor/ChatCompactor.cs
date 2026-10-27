using CodeSwitchX.Core.Yard;
using LittleGod.Llm.Abstractions;
using LittleGod.Llm.ClaudeCli;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Conductor;

/// <summary>Compacts a chat's conversation as <c>/compact</c> typed in its tab would (#226).</summary>
public interface IChatCompactor
{
    /// <summary>
    /// Compacts the conversation on disk; returns once it is done. Its tab must be closed: an open one holds the whole
    /// conversation and would carry on from that.
    /// </summary>
    /// <param name="folder">The folder the chat runs in: Claude Code finds a conversation by it.</param>
    /// <param name="keep">What the summary is to keep ("the test plan"); null for a plain <c>/compact</c>.</param>
    /// <exception cref="YardActionException">It was not compacted; the message says why.</exception>
    Task CompactAsync(string sessionId, string folder, string? keep, CancellationToken ct);

    /// <summary>
    /// Names the chat as <c>/rename</c> typed in its tab would. VS Code lists, and opens again, only a chat with a name: one
    /// that was only ever messaged by another session (Raven) has none (#226). Its tab may be open: the tab takes the name
    /// up at its next write, and goes on from its own last step (spike, #228).
    /// </summary>
    /// <exception cref="YardActionException">It was not named; the message says why.</exception>
    Task NameAsync(string sessionId, string folder, string name, CancellationToken ct);
}

/// <summary>
/// Claude Code compacts it, headless, through the shared package (#324): <c>claude -p --resume &lt;id&gt;</c> in the chat's
/// folder with the command as its prompt, which writes the compact boundary and the summary into the conversation as the
/// tab's own <c>/compact</c> does (spike, 2026-10-08). Only that conversation: one that is gone is not started again as a
/// new session, which VS Code would list as a chat of its own. It runs as VS Code's Claude Code (<c>CLAUDE_CODE_ENTRYPOINT</c>),
/// which keeps the chat in VS Code's session list, and with no model given: the chat's own. It runs with hooks off: its
/// SessionStart and SessionEnd carry the chat's own id, and would tell CodeSwitchX that the chat ended while its tab still
/// runs. The user's own hooks (a PreCompact one) do not run for it either.
/// </summary>
/// <param name="claude">The package's <see cref="ClaudeCliChatClient"/>, made with <see cref="Options"/>.</param>
public sealed class ChatCompactor(IChatClient claude, TimeProvider time, ILogger<ChatCompactor> logger) : IChatCompactor
{
    /// <summary>How long a compaction may take: a short chat takes about 15 seconds, a long one a minute or two.</summary>
    internal static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    private const string TheCli = "The Claude CLI ";

    /// <summary>
    /// How the compactor's client runs Claude Code: with the user's settings, Claude Code's own prompt and no tool or MCP
    /// flags, as before the package (#324).
    /// </summary>
    public static ClaudeCliOptions Options() => new()
    {
        Isolation = ClaudeIsolation.Custom,
        DefaultSystemPrompt = null,
        KeepApiKeyEnvironment = true,
    };

    public Task CompactAsync(string sessionId, string folder, string? keep, CancellationToken ct) =>
        RunAsync(sessionId, folder, Command(keep), "compact", "Compacted", "Compacting", ct);

    public Task NameAsync(string sessionId, string folder, string name, CancellationToken ct) =>
        RunAsync(sessionId, folder, $"/rename {OneLine(name)}", "name", "Named", "Naming", ct);

    /// <summary>Runs the command on the conversation; returns once Claude Code has done it.</summary>
    /// <param name="what">What the command does to the chat, for what is said: "compact".</param>
    private async Task RunAsync(string sessionId, string folder, string command, string what, string done, string doing, CancellationToken ct)
    {
        var options = new ChatOptions { ConversationId = sessionId }.WithClaudeCliSlashCommand().WithClaudeCli(c =>
        {
            c.WorkingDirectory = folder;
            c.Environment = new Dictionary<string, string?> { ["CLAUDE_CODE_ENTRYPOINT"] = "claude-vscode" };
            c.ExtraArguments = ["--settings", ClaudeCliBrain.NoHooks];
        });
        using var limit = new CancellationTokenSource(Limit, time);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
        string why;
        try
        {
            var response = await claude.GetResponseAsync([new ChatMessage(ChatRole.User, command)], options, both.Token).ConfigureAwait(false);
            if (response.AdditionalProperties?.ContainsKey(ClaudeCliKeys.LocalCommand) == true)
            {
                logger.LogInformation("{Done} chat {Id}", done, sessionId);
                return;
            }

            why = "it answered the command as a question instead of running it.";
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            logger.LogWarning("{Doing} chat {Id} took longer than {Limit}; stopped", doing, sessionId, Limit);
            throw new YardActionException($"{doing} the chat took longer than {Limit.TotalMinutes:0} minutes and was stopped; it is as it was.");
        }
        catch (ClaudeCliSessionGoneException)
        {
            why = "it no longer has the chat's conversation.";
        }
        catch (LlmException ex)
        {
            why = ex.UserMessage.StartsWith(TheCli, StringComparison.Ordinal) ? "it " + ex.UserMessage[TheCli.Length..] : ex.UserMessage;
        }

        logger.LogWarning("{Doing} chat {Id} failed: {Why}", doing, sessionId, why);
        throw new YardActionException($"Claude Code did not {what} the chat: {why}");
    }

    /// <summary>The command, with what to keep on one line: <c>/compact keep the test plan</c>.</summary>
    internal static string Command(string? keep) => OneLine(keep) is { Length: > 0 } words ? $"/compact {words}" : "/compact";

    private static string OneLine(string? text) => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
