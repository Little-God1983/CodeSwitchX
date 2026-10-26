using LittleGod.Llm.ClaudeCli;
using Microsoft.Extensions.AI;

namespace CodeSwitchX.Conductor;

/// <summary>
/// What a brain uses of the package's <see cref="ClaudeCliSession"/> (#314). The package keeps its process launcher to itself,
/// so the brain's tests run over a fake of this, made from the options the brain set up.
/// </summary>
internal interface IClaudeSession : IDisposable
{
    /// <inheritdoc cref="ClaudeCliSession.AskAsync"/>
    IAsyncEnumerable<ChatResponseUpdate> AskAsync(string text, CancellationToken ct);

    /// <inheritdoc cref="ClaudeCliSession.WarmUp"/>
    void WarmUp();

    /// <inheritdoc cref="ClaudeCliSession.Rest"/>
    void Rest();

    /// <inheritdoc cref="ClaudeCliSession.RestartWhenIdle"/>
    void RestartWhenIdle();
}

/// <summary>The package's session, as a brain uses it.</summary>
internal sealed class ClaudeSession(ClaudeCliSession session) : IClaudeSession
{
    public IAsyncEnumerable<ChatResponseUpdate> AskAsync(string text, CancellationToken ct) => session.AskAsync(text, ct);

    public void WarmUp() => session.WarmUp();

    public void Rest() => session.Rest();

    public void RestartWhenIdle() => session.RestartWhenIdle();

    public void Dispose() => session.Dispose();
}
