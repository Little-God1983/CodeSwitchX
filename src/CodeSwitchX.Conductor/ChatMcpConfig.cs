using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Yard;

namespace CodeSwitchX.Conductor;

/// <summary>
/// The MCP config of a window chat's brain: the app's <c>mcp.json</c> with a header that names the window, so the Yard's
/// tools act on it by default. It carries the access token, so it is the user's alone, like <c>mcp.json</c>; and it is a
/// file, as a token on the command line would end up in process listings and their logs.
/// </summary>
public static class ChatMcpConfig
{
    /// <summary>Writes <paramref name="target"/> from <paramref name="source"/> for the window; throws as file and JSON reading do.</summary>
    public static void Write(string source, string target, Guid workspaceId)
    {
        var config = JsonNode.Parse(File.ReadAllText(source))?.AsObject() ?? throw new JsonException($"{source} is empty.");
        var server = config["mcpServers"]?[YardMcp.ServerName]?.AsObject() ?? throw new JsonException($"{source} names no {YardMcp.ServerName} server.");
        var headers = server["headers"] as JsonObject ?? [];
        headers[YardMcp.ChatHeader] = workspaceId.ToString("D");
        server["headers"] = headers;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, config.ToJsonString());
        SecretFile.RestrictToCurrentUser(temporary);
        File.Move(temporary, target, overwrite: true);
    }
}
