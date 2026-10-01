namespace CodeSwitchX.Conductor;

/// <summary>
/// What answers the user behind the Raven panel. Provider-neutral: <see cref="ClaudeCliBrain"/> runs Claude Code, and a
/// later brain on <c>Microsoft.Extensions.AI</c>'s <c>IChatClient</c> (LM Studio, a local runner) would load the same MCP
/// tools as <c>AIFunction</c>s and report the same events. One conversation, one turn at a time: a turn asked for while
/// another runs waits for it.
/// </summary>
public interface IConductorBrain : IAsyncDisposable
{
    /// <summary>
    /// Asks one turn and streams what the brain does in it: its reply in pieces, the tools it calls. The sequence ends
    /// with the turn; one that could not be answered ends with a <see cref="BrainFailed"/>. Never throws but for
    /// cancellation.
    /// </summary>
    IAsyncEnumerable<BrainEvent> AskAsync(string text, CancellationToken ct);

    /// <summary>Gets ready for a turn that is about to come (the user started talking), so its answer does not wait for a start. Never throws.</summary>
    void WarmUp();
}

/// <summary>Something the brain did during a turn.</summary>
public abstract record BrainEvent;

/// <summary>
/// The question has gone in: the brain has it now, in its conversation, whatever becomes of the turn. Comes before any
/// other event of the turn's but its notices; a turn that ends before it never took the question.
/// </summary>
public sealed record BrainQuestionSent : BrainEvent;

/// <summary>The next piece of the reply.</summary>
public sealed record BrainText(string Delta) : BrainEvent;

/// <summary>The brain called a tool.</summary>
/// <param name="Tool">The tool's own name (<c>list_chats</c>), without the prefix a provider adds.</param>
/// <param name="Input">Its arguments as compact JSON; <c>{}</c> for none.</param>
public sealed record BrainToolCall(string Id, string Tool, string Input) : BrainEvent;

/// <summary>A tool call came back.</summary>
public sealed record BrainToolResult(string Id, bool Failed) : BrainEvent;

/// <summary>Something the user should know about the brain itself: it was restarted, it cannot see the Yard.</summary>
public sealed record BrainNotice(string Text, bool Warning) : BrainEvent;

/// <summary>The turn ended without an answer, and why, in words for the user.</summary>
public sealed record BrainFailed(string Reason) : BrainEvent;
