using CodeSwitchX.Core.Yard;
using Microsoft.AspNetCore.Http;

namespace CodeSwitchX.Ingest.Mcp;

/// <summary>
/// The Raven chat a tool call comes from: a window's chat names its workspace in <see cref="YardMcp.ChatHeader"/>, and
/// the tools act on that window when the brain names no other. Chat 0, the Yard, sends none: no window in particular.
/// </summary>
public sealed record ChatScope(Guid? WorkspaceId)
{
    public static readonly ChatScope None = new((Guid?)null);

    public static ChatScope Of(HttpContext? context) =>
        context?.Request.Headers[YardMcp.ChatHeader].ToString() is { Length: > 0 } header && Guid.TryParse(header, out var id) ? new ChatScope(id) : None;
}
