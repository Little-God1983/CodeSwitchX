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

/// <summary>A turn a brain took on its own, and what it said in it.</summary>
/// <param name="WorkspaceId">The window whose Raven chat's brain took it; null for chat 0, the Yard.</param>
public sealed record UnaskedTurn(Guid? WorkspaceId, string Text);
