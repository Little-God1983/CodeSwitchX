using System.Text.Json;

namespace CodeSwitchX.Conductor;

/// <summary>A line of <c>claude -p --output-format stream-json</c> that matters to the brain.</summary>
internal abstract record ClaudeLine;

/// <summary><c>system/init</c>, sent at the start of every turn.</summary>
/// <param name="McpServers">Each MCP server's name and whether it connected ("connected", "failed", "pending").</param>
/// <param name="PermissionMode">The mode it runs in: "auto" asked for with a model that cannot do it runs as "default".</param>
internal sealed record ClaudeInit(string? Model, IReadOnlyDictionary<string, string> McpServers, string? PermissionMode = null) : ClaudeLine;

/// <summary>What the line says the brain did.</summary>
internal sealed record ClaudeEvents(IReadOnlyList<BrainEvent> Events) : ClaudeLine;

/// <summary><c>result</c>: the turn is over; <paramref name="Error"/> says why it failed, null when it did not.</summary>
internal sealed record ClaudeTurnOver(string? Error) : ClaudeLine;

/// <summary>
/// Reads the stream-json lines of Claude Code, as run with <c>--verbose --include-partial-messages</c> (checked against
/// CLI 2.1.285): the reply comes as <c>stream_event</c> text deltas, a tool call in the <c>assistant</c> message that
/// holds it, its result in the next <c>user</c> message, and the turn ends with a <c>result</c> line. Lines of a
/// subagent (a <c>parent_tool_use_id</c>) and everything else are no news. Never throws: a line that is no JSON, or not
/// the shape expected, is null.
/// </summary>
internal static class ClaudeStream
{
    public static ClaudeLine? Read(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "type") is not { } type)
            {
                return null;
            }

            if (root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String)
            {
                return null;
            }

            return type switch
            {
                "system" when Text(root, "subtype") == "init" => Init(root),
                "stream_event" => Delta(root),
                "assistant" => ToolCalls(root),
                "user" => ToolResults(root),
                "result" => new ClaudeTurnOver(Error(root)),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether the line is a message of the main agent's: the model is answering. Never throws.</summary>
    public static bool IsAssistant(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && Text(root, "type") == "assistant"
                && !(root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The tool's own name: Claude Code calls an MCP tool <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>.</summary>
    public static string ToolName(string name)
    {
        if (!name.StartsWith("mcp__", StringComparison.Ordinal))
        {
            return name;
        }

        var split = name.IndexOf("__", "mcp__".Length, StringComparison.Ordinal);
        return split < 0 ? name : name[(split + 2)..];
    }

    private static ClaudeInit Init(JsonElement root)
    {
        var servers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("mcp_servers", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var server in list.EnumerateArray())
            {
                if (Text(server, "name") is { } name)
                {
                    servers[name] = Text(server, "status") ?? "unknown";
                }
            }
        }

        return new ClaudeInit(Text(root, "model"), servers, Text(root, "permissionMode"));
    }

    private static ClaudeEvents? Delta(JsonElement root)
    {
        if (root.TryGetProperty("event", out var e) && Text(e, "type") == "content_block_delta"
            && e.TryGetProperty("delta", out var delta) && Text(delta, "type") == "text_delta" && Text(delta, "text") is { Length: > 0 } text)
        {
            return new ClaudeEvents([new BrainText(text)]);
        }

        return null;
    }

    private static ClaudeEvents? ToolCalls(JsonElement root)
    {
        var events = Content(root)
            .Where(c => Text(c, "type") == "tool_use" && Text(c, "id") is not null && Text(c, "name") is not null)
            .Select(c => (BrainEvent)new BrainToolCall(Text(c, "id")!, ToolName(Text(c, "name")!),
                c.TryGetProperty("input", out var input) ? input.GetRawText() : "{}"))
            .ToList();
        return events.Count > 0 ? new ClaudeEvents(events) : null;
    }

    private static ClaudeEvents? ToolResults(JsonElement root)
    {
        var events = Content(root)
            .Where(c => Text(c, "type") == "tool_result" && Text(c, "tool_use_id") is not null)
            .Select(c => (BrainEvent)new BrainToolResult(Text(c, "tool_use_id")!,
                c.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True))
            .ToList();
        return events.Count > 0 ? new ClaudeEvents(events) : null;
    }

    private static string? Error(JsonElement root)
    {
        if (!(root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True) && Text(root, "subtype") is null or "success")
        {
            return null;
        }

        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
            && errors.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).FirstOrDefault(e => e is { Length: > 0 }) is { } first)
        {
            return first;
        }

        return Text(root, "result") is { Length: > 0 } result ? result : Text(root, "subtype") ?? "unknown error";
    }

    private static IEnumerable<JsonElement> Content(JsonElement root) =>
        root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object).ToList()
            : [];

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
