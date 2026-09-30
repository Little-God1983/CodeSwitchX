using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Ingest.Api;

namespace CodeSwitchX.Ingest.Mcp;

/// <summary>
/// The <c>mcp.json</c> Claude Code is started with (<c>--mcp-config</c>) to reach CodeSwitchX's MCP server: its loopback
/// address and the same token <c>/events</c> takes. The token makes the file a secret, so it is readable by the current
/// user only, like the token file.
/// </summary>
public static class McpConfigFile
{
    public static void Write(string file, int port, string token)
    {
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [YardMcp.ServerName] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = $"http://127.0.0.1:{port}{YardMcp.Route}",
                    ["headers"] = new JsonObject { ["Authorization"] = $"Bearer {token}" },
                },
            },
        };

        AtomicFile.Replace(file, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ".tmp");
        AccessTokenStore.RestrictToCurrentUser(file);
    }
}
