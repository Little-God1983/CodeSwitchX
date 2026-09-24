using System.Globalization;
using System.Text.Json;
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Ingest.Transcripts;

public static class TranscriptLineParser
{
    /// <summary>Claude Code writes this user line when the turn is interrupted with Esc ("... by user" or "... by user for tool use").</summary>
    private const string InterruptPrefix = "[Request interrupted by user";

    private static readonly string[] MetaPrefixes = ["<command-name>", "<local-command-stdout>", "<local-command-stderr>", "<system-reminder>", "<command-message>"];

    public static TranscriptLine? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            try
            {
                return Read(document.RootElement);
            }
            catch (InvalidOperationException)
            {
                // Valid JSON that is not valid text: JSON.stringify writes half of a surrogate pair (a string cut inside an
                // emoji) as an escape, and JsonElement.GetString throws on it. Skipped like any other corrupt line.
                return null;
            }
        }
    }

    private static TranscriptLine? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = GetString(root, "type") ?? string.Empty;
        var timestamp = GetString(root, "timestamp") is { } ts && DateTimeOffset.TryParse(ts, null, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : (DateTimeOffset?)null;
        var sessionId = GetString(root, "sessionId");
        var cwd = GetString(root, "cwd");
        root.TryGetProperty("message", out var message);

        switch (type)
        {
            case "assistant":
            {
                var usage = ParseUsage(message);
                var hasToolUse = message.ValueKind == JsonValueKind.Object
                    && message.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Array
                    && content.EnumerateArray().Any(b => GetString(b, "type") == "tool_use");
                return new AssistantLine(type, timestamp, sessionId, cwd, GetString(message, "id"), GetString(message, "model"), usage, hasToolUse);
            }

            case "user":
            {
                var isMeta = root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True;
                var (text, isToolResult) = ExtractUserText(message);
                var isInterrupt = text is not null && text.StartsWith(InterruptPrefix, StringComparison.Ordinal);
                if (isInterrupt || (text is not null && MetaPrefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal))))
                {
                    isMeta = true;
                }

                return new UserLine(type, timestamp, sessionId, cwd, text, isToolResult, isMeta, isInterrupt);
            }

            // Claude Code's generated chat title: older versions wrote `summary` lines, current ones write `ai-title`.
            case "summary":
            case "ai-title":
                return GetString(root, type == "summary" ? "summary" : "aiTitle") is { Length: > 0 } title
                    ? new SummaryLine(type, timestamp, sessionId, cwd, title)
                    : new OtherLine(type, timestamp, sessionId, cwd);

            default:
                return new OtherLine(type, timestamp, sessionId, cwd);
        }
    }

    private static TokenUsage? ParseUsage(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new TokenUsage(
            GetLong(usage, "input_tokens"),
            GetLong(usage, "output_tokens"),
            GetLong(usage, "cache_creation_input_tokens"),
            GetLong(usage, "cache_read_input_tokens"));
    }

    private static (string? Text, bool IsToolResult) ExtractUserText(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var content))
        {
            return (null, false);
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return (content.GetString(), false);
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return (null, false);
        }

        var isToolResult = false;
        foreach (var block in content.EnumerateArray())
        {
            switch (GetString(block, "type"))
            {
                case "text" when GetString(block, "text") is { Length: > 0 } text:
                    return (text, false);
                case "tool_result":
                    isToolResult = true;
                    break;
            }
        }

        return (null, isToolResult);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var l) ? l : 0;
}
