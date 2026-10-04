namespace CodeSwitchX.Core.Yard;

/// <summary>Where the Yard's MCP tools are served, shared by the server that serves them and the brain that calls them.</summary>
public static class YardMcp
{
    /// <summary>The server's name in <c>mcp.json</c>; Claude Code calls its tools <c>mcp__codeswitchx__…</c>.</summary>
    public const string ServerName = "codeswitchx";

    /// <summary>Where the server listens under the loopback port.</summary>
    public const string Route = "/mcp";

    /// <summary>
    /// The header a window chat's brain sends with every tool call: the workspace id of its window, which the tools act on
    /// when no other is named. Chat 0's brain sends <see cref="OverviewChat"/>; a brain without it acts on no window in particular.
    /// </summary>
    public const string ChatHeader = "X-CodeSwitchX-Chat";

    /// <summary>What chat 0, the Yard's overview, sends as <see cref="ChatHeader"/>: it sees no card's text and answers none.</summary>
    public const string OverviewChat = "yard";
}
