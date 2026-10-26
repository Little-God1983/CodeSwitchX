using System.Runtime.CompilerServices;
using LittleGod.Llm.Abstractions;
using LittleGod.Llm.ClaudeCli;
using Microsoft.Extensions.AI;

namespace CodeSwitchX.Conductor.Tests;

/// <summary>
/// The package's session as the brain sees it (#314): it answers each question with <see cref="Answer"/>, and keeps how the
/// brain had set the options up when each question was read and each warm-up asked for. Its callbacks are the brain's, in
/// <see cref="Options"/>, for a test to call as the session would.
/// </summary>
internal sealed class FakeClaudeSession(ClaudeCliSessionOptions options, ClaudeCliOptions cli) : IClaudeSession
{
    public ClaudeCliSessionOptions Options { get; } = options;

    public ClaudeCliOptions Cli { get; } = cli;

    /// <summary>The questions, as they were asked (before they were read).</summary>
    public List<string> Asked { get; } = [];

    /// <summary>How the options stood as each question was read: what the session would start a process with.</summary>
    public List<Seen> Read { get; } = [];

    /// <summary>How the options stood at each warm-up.</summary>
    public List<Seen> WarmUps { get; } = [];

    public int Rests { get; private set; }

    public int Restarts { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>What each question is answered with; a short reply by default.</summary>
    public Func<string, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> Answer { get; set; } =
        (_, _) => Updates(new ClaudeCliQuestionSentContent(), new TextContent("Hi."));

    /// <param name="Servers">The MCP servers' JSON.</param>
    public sealed record Seen(string Model, LlmEffort Effort, string? Servers, ClaudeCliConversation? Conversation);

    public IAsyncEnumerable<ChatResponseUpdate> AskAsync(string text, CancellationToken ct)
    {
        Asked.Add(text);
        return ReadAsync(text, ct);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> ReadAsync(string text, [EnumeratorCancellation] CancellationToken ct)
    {
        Read.Add(Now());
        await foreach (var update in Answer(text, ct).WithCancellation(ct))
        {
            yield return update;
        }
    }

    public void WarmUp() => WarmUps.Add(Now());

    public void Rest() => Rests++;

    public void RestartWhenIdle() => Restarts++;

    public void Dispose() => Disposed = true;

    private Seen Now() => new(Options.Model, Options.Effort, Options.Cli.McpServersJson, Options.Conversation);

    /// <summary>An update for each content, in order, as the session streams them.</summary>
    public static async IAsyncEnumerable<ChatResponseUpdate> Updates(params AIContent[] contents)
    {
        foreach (var content in contents)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [content]);
        }
    }

    /// <summary>The contents, then <paramref name="failure"/>, as a turn that fails after it began.</summary>
    public static async IAsyncEnumerable<ChatResponseUpdate> Failing(Exception failure, params AIContent[] before)
    {
        await foreach (var update in Updates(before))
        {
            yield return update;
        }

        throw failure;
    }
}
