namespace CodeSwitchX.Core.Yard;

/// <summary>Where the Yard's MCP tools are served, shared by the server that serves them and the brain that calls them.</summary>
public static class YardMcp
{
    /// <summary>The server's name in <c>mcp.json</c>; Claude Code calls its tools <c>mcp__codeswitchx__…</c>.</summary>
    public const string ServerName = "codeswitchx";

    /// <summary>Where the server listens under the loopback port.</summary>
    public const string Route = "/mcp";
}
