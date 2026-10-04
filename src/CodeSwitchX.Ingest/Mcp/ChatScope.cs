using CodeSwitchX.Core.Yard;
using Microsoft.AspNetCore.Http;

namespace CodeSwitchX.Ingest.Mcp;

/// <summary>
/// The Raven chat a tool call comes from: a window's chat names its workspace in <see cref="YardMcp.ChatHeader"/>, and
/// the tools act on that window when the brain names no other. Chat 0, the Yard, is the overview (#124): no window in
/// particular, and it knows the window chats by their summaries only, so it is shown no card's text and answers none.
/// </summary>
/// <param name="Overview">Chat 0's: cards are named, not read, and are answered in their window's chat.</param>
public sealed record ChatScope(Guid? WorkspaceId, bool Overview = false)
{
    public static readonly ChatScope None = new((Guid?)null);

    public static readonly ChatScope Yard = new(null, Overview: true);

    public static ChatScope Of(HttpContext? context) => context?.Request.Headers[YardMcp.ChatHeader].ToString() switch
    {
        YardMcp.OverviewChat => Yard,
        { Length: > 0 } header when Guid.TryParse(header, out var id) => new ChatScope(id),
        _ => None,
    };
}
