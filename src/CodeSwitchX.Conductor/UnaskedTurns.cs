namespace CodeSwitchX.Conductor;

/// <summary>
/// The turns Raven's brains take on their own, which no question of the user's started: a message from another Claude
/// session (a chat's <c>SendMessage</c> back to Raven) starts one between questions (#181). The panel shows what the brain
/// said in them in its Raven chat. Thread-safe; reported from the brain's own thread.
/// </summary>
public sealed class UnaskedTurns
{
    public event Action<UnaskedTurn>? Taken;

    public void Report(UnaskedTurn turn) => Taken?.Invoke(turn);
}

/// <summary>A turn a brain took on its own, what it said in it and what it did with its tools.</summary>
/// <param name="WorkspaceId">The window whose Raven chat's brain took it; null for chat 0, the Yard.</param>
/// <param name="Text">What it said; empty when it said nothing. Of a failed turn, what it said before it failed.</param>
/// <param name="Calls">The tools it called, in order: what a chat's message made Raven do is never done unseen.</param>
/// <param name="Failure">Why the turn ended without its answer, in words for the user; null when it did not.</param>
public sealed record UnaskedTurn(Guid? WorkspaceId, string Text, IReadOnlyList<UnaskedCall> Calls, string? Failure);

/// <summary>A tool a brain called in a turn of its own, and whether the call failed.</summary>
public sealed record UnaskedCall(BrainToolCall Call, bool Failed);
