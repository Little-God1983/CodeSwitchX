using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Yard;

namespace CodeSwitchX.Conductor;

/// <summary>
/// The MCP config of a Raven chat's brain: the app's <c>mcp.json</c> with a header that names the chat: a window's, whose
/// tools act on it by default, or chat 0, the overview (<see cref="YardMcp.OverviewChat"/>). It carries the access token, so it is the user's alone, like <c>mcp.json</c>; and it is a
/// file, as a token on the command line would end up in process listings and their logs.
/// </summary>
public static class ChatMcpConfig
{
    /// <summary>Writes <paramref name="target"/> from <paramref name="source"/> for the chat; throws as file and JSON reading do.</summary>
    /// <param name="chat">The header's value: the window's workspace id, or <see cref="YardMcp.OverviewChat"/>.</param>
    /// <remarks>As <c>mcp.json</c> is: replaced in one step, then made the user's alone.</remarks>
    public static void Write(string source, string target, string chat)
    {
        var config = JsonNode.Parse(File.ReadAllText(source))?.AsObject() ?? throw new JsonException($"{source} is empty.");
        var server = config["mcpServers"]?[YardMcp.ServerName]?.AsObject() ?? throw new JsonException($"{source} names no {YardMcp.ServerName} server.");
        var headers = server["headers"] as JsonObject ?? [];
        headers[YardMcp.ChatHeader] = chat;
        server["headers"] = headers;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        AtomicFile.Replace(target, config.ToJsonString(), ".tmp");
        SecretFile.RestrictToCurrentUser(target);
    }

    /// <summary>
    /// Deletes the configs in the folder: an earlier run that crashed left them, the token in them. Each brain writes its
    /// own again as it starts. Never throws; one that cannot be deleted stays.
    /// </summary>
    public static void Clear(string folder)
    {
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
