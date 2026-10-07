using System.Text.Json;

namespace CodeSwitchX.Conductor;

/// <summary>A line of <c>claude -p --output-format stream-json</c> that matters to the brain.</summary>
internal abstract record ClaudeLine;

/// <summary><c>system/init</c>, sent at the start of every turn.</summary>
/// <param name="McpServers">Each MCP server's name and whether it connected ("connected", "failed", "pending").</param>
/// <param name="PermissionMode">The mode it runs in: "auto" asked for with a model that cannot do it runs as "default".</param>
/// <param name="Tools">The tools it has, built-in and MCP; null when the line does not list them.</param>
internal sealed record ClaudeInit(string? Model, IReadOnlyDictionary<string, string> McpServers, string? PermissionMode = null,
    IReadOnlyList<string>? Tools = null) : ClaudeLine;

/// <summary>What the line says the brain did.</summary>
internal sealed record ClaudeEvents(IReadOnlyList<BrainEvent> Events) : ClaudeLine;

/// <summary><c>result</c>: the turn is over; <paramref name="Error"/> says why it failed, null when it did not.</summary>
internal sealed record ClaudeTurnOver(string? Error) : ClaudeLine;

/// <summary>An <c>assistant</c> message of the main agent's without a tool call: the model is answering.</summary>
internal sealed record ClaudeAnswer : ClaudeLine;

/// <summary>
/// A line written to it, echoed back as it takes it into a turn (<c>--replay-user-messages</c>): when that turn begins, or
/// when the running one folds it in at a tool call (seen with CLI 2.1.286). A message from another Claude session
/// (<c>SendMessage</c>) is echoed too, with an <c>origin</c> of kind "peer" (seen with CLI 2.1.292).
/// </summary>
/// <param name="Id">The line's <c>uuid</c>: the one it was written with, which CLI 2.1.292 echoes back; null for none.</param>
/// <param name="FromPeer">It came from another session, not from standard input.</param>
internal sealed record ClaudeTaken(string? Id, bool FromPeer) : ClaudeLine;

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
                "assistant" => (ClaudeLine?)ToolCalls(root) ?? Answer(root),
                "user" when root.TryGetProperty("isReplay", out var replay) && replay.ValueKind == JsonValueKind.True => Taken(root),
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

        var tools = root.TryGetProperty("tools", out var named) && named.ValueKind == JsonValueKind.Array
            ? named.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToList()
            : null;
        return new ClaudeInit(Text(root, "model"), servers, Text(root, "permissionMode"), tools);
    }

    private static ClaudeTaken Taken(JsonElement root) =>
        new(Text(root, "uuid"), root.TryGetProperty("origin", out var origin) && Text(origin, "kind") == "peer");

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

    /// <summary>
    /// Not the message Claude Code writes itself when the API call failed (model <c>&lt;synthetic&gt;</c>, with an
    /// <c>error</c>, such as for a model that does not exist): the <c>result</c> after it says the turn failed.
    /// </summary>
    private static ClaudeAnswer? Answer(JsonElement root) =>
        root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
        && Text(root, "error") is null && Text(message, "model") != "<synthetic>"
            ? new ClaudeAnswer()
            : null;

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
