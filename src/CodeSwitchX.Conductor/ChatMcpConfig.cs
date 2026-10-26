using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Yard;

namespace CodeSwitchX.Conductor;

/// <summary>
/// The MCP servers of a Raven chat's brain: the app's <c>mcp.json</c> with a header that names the chat: a window's, whose
/// tools act on it by default, or chat 0, the overview (<see cref="YardMcp.OverviewChat"/>). They carry the access token:
/// the brain's session writes them into a file of its process's own (a token on the command line would end up in process
/// listings and their logs), which goes with the process.
/// </summary>
public static class ChatMcpConfig
{
    /// <summary>The servers of <paramref name="source"/>, as the JSON object of servers by name, with the chat's header; throws as file and JSON reading do.</summary>
    /// <param name="chat">The header's value: the window's workspace id, or <see cref="YardMcp.OverviewChat"/>; null for no header.</param>
    public static string Servers(string source, string? chat)
    {
        var config = JsonNode.Parse(File.ReadAllText(source))?.AsObject() ?? throw new JsonException($"{source} is empty.");
        var servers = config["mcpServers"] as JsonObject ?? throw new JsonException($"{source} has no mcpServers.");
        var server = servers[YardMcp.ServerName] as JsonObject ?? throw new JsonException($"{source} names no {YardMcp.ServerName} server.");
        if (chat is not null)
        {
            var headers = server["headers"] as JsonObject ?? [];
            headers[YardMcp.ChatHeader] = chat;
            server["headers"] = headers;
        }

        return servers.ToJsonString();
    }

    /// <summary>
    /// Readies the folder the brains' processes keep their MCP configs in (#314), as the app starts, before any brain runs:
    /// deletes what an earlier run that crashed left there (configs, and the folders of the processes), the token in them,
    /// and makes the folder the user's alone, as <c>mcp.json</c> is, which every process's folder and config inherit. Each
    /// process writes its own again as it starts. Never throws; what cannot be deleted stays, and a folder whose access
    /// cannot be set keeps its own.
    /// </summary>
    public static void Reset(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            SecretFile.RestrictFolderToCurrentUser(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
        }

        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(folder))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            foreach (var kept in Directory.EnumerateDirectories(folder))
            {
                try
                {
                    Directory.Delete(kept, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
