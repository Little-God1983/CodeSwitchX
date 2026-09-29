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
            var root = document.RootElement;
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

                // The name the user gave the chat with /rename; Claude Code's session list shows it before the generated title.
                case "custom-title":
                    return GetString(root, "customTitle") is { Length: > 0 } custom
                        ? new CustomTitleLine(type, timestamp, sessionId, cwd, custom)
                        : new OtherLine(type, timestamp, sessionId, cwd);

                default:
                    return new OtherLine(type, timestamp, sessionId, cwd);
            }
        }
    }

    private static TokenUsage? ParseUsage(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // cache_creation_input_tokens is every cache write; cache_creation breaks it down by cache. The 1-hour writes are
        // their own count (they cost more), and the rest stays the 5-minute count, so the sum of both is the total. The
        // total is at least the breakdown's sum, so a line that reports the breakdown alone loses nothing, and a negative
        // count in either is no write.
        var cacheWrites = GetLong(usage, "cache_creation_input_tokens");
        var oneHour = 0L;
        if (usage.TryGetProperty("cache_creation", out var creation) && creation.ValueKind == JsonValueKind.Object)
        {
            oneHour = Math.Max(0, GetLong(creation, "ephemeral_1h_input_tokens"));
            cacheWrites = Math.Max(cacheWrites, Math.Max(0, GetLong(creation, "ephemeral_5m_input_tokens")) + oneHour);
        }

        return new TokenUsage(
            GetLong(usage, "input_tokens"),
            GetLong(usage, "output_tokens"),
            cacheWrites - oneHour,
            GetLong(usage, "cache_read_input_tokens"),
            oneHour);
    }

    private static (string? Text, bool IsToolResult) ExtractUserText(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var content))
        {
            return (null, false);
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return (ReadString(content), false);
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
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? ReadString(value) : null;

    private static string? ReadString(JsonElement value) => JsonStrings.TryRead(value);

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var l) ? l : 0;
}
