using CodeSwitchX.Core.Yard;
using Microsoft.AspNetCore.Http;

namespace CodeSwitchX.Ingest.Mcp;

/// <summary>
/// The Raven chat a tool call comes from: a window's chat names its workspace in <see cref="YardMcp.ChatHeader"/>, and
/// the tools act on that window when the brain names no other. Chat 0, the Yard, is the overview (#124): no window in
/// particular, and it knows the window chats by their summaries only, so it is shown no card's text and answers none.
/// </summary>
/// <param name="Overview">Chat 0's: cards are named, not read, and are answered in their window's chat.</param>
/// <param name="Unknown">A chat header was sent that names no Raven chat: it acts on no window, and is refused what acts (#193).</param>
public sealed record ChatScope(Guid? WorkspaceId, bool Overview = false, bool Unknown = false)
{
    public static readonly ChatScope None = new((Guid?)null);

    public static readonly ChatScope Yard = new(null, Overview: true);

    /// <summary>What its brain sends as <see cref="YardMcp.ChatHeader"/>, by which <see cref="AskedChats"/> knows it; null for no Raven chat.</summary>
    public string? Key => YardMcp.ChatKey(WorkspaceId, Overview);

    public static ChatScope Of(HttpContext? context) => context?.Request.Headers[YardMcp.ChatHeader].ToString() switch
    {
        null or "" => None,
        YardMcp.OverviewChat => Yard,
        var header when Guid.TryParse(header, out var id) => new ChatScope(id),
        _ => new ChatScope(null, Unknown: true),
    };
}
