using System.Text;
using System.Text.Json;
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Ingest.Transcripts;

/// <summary>
/// A chat's conversation in short, for a summary of it (#234): what it was first asked, then its latest steps, oldest
/// first: the user's prompts (a message from another session, how Raven hands a chat its task, says so), the chat's
/// replies, the tools it used with the file or command each was for, and the summary Claude Code wrote when it compacted
/// the chat. Only the main chat counts, no sub-agent's. Read from the file's start for the first prompt and from its end
/// for the rest, so a long conversation costs no more than a short one.
/// </summary>
public static class TranscriptDigest
{
    /// <summary>How much of the file's end is read for the latest steps.</summary>
    public const int TailBytes = 2 * 1024 * 1024;

    /// <summary>How much of the file's start is looked through for the first prompt.</summary>
    public const int HeadBytes = 512 * 1024;

    /// <summary>The most one step is given: a long reply says what it is about in its start.</summary>
    internal const int MaxStepChars = 1200;

    /// <summary>The note where steps between the first prompt and the latest ones were left out.</summary>
    internal const string LeftOut = "[earlier steps left out]";

    /// <summary>
    /// The digest, at most about <paramref name="maxChars"/> long: the first prompt, and the latest steps that fit. Null
    /// when the file has no step or cannot be read.
    /// </summary>
    public static string? Read(string? path, int maxChars = 24_000)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string head, tail;
        bool whole;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            whole = stream.Length <= TailBytes;
            head = whole ? "" : ReadAt(stream, 0, HeadBytes);
            tail = ReadAt(stream, Math.Max(0, stream.Length - TailBytes), TailBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // A cut line at either edge of what was read does not parse, and is passed over.
        var steps = tail.Split('\n').Select(StepOf).OfType<string>().ToList();
        var first = (whole ? tail : head).Split('\n').Select(PromptOf).OfType<string>().FirstOrDefault();
        if (steps.Count == 0 && first is null)
        {
            return null;
        }

        var kept = new List<string>();
        var size = first?.Length ?? 0;
        for (var i = steps.Count - 1; i >= 0 && size + steps[i].Length <= maxChars; i--)
        {
            kept.Insert(0, steps[i]);
            size += steps[i].Length + 1;
        }

        // The first prompt says what the chat is for: it stays when the steps after it are too many to keep.
        var all = whole && kept.Count == steps.Count;
        var text = new StringBuilder();
        if (!all && first is not null)
        {
            text.Append("First asked: ").Append(first[(first.IndexOf(": ", StringComparison.Ordinal) + 2)..]).Append('\n');
        }

        if (!all)
        {
            text.Append(LeftOut).Append('\n');
        }

        foreach (var step in kept)
        {
            text.Append(step).Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    private static string ReadAt(FileStream stream, long start, int count)
    {
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[(int)Math.Min(count, stream.Length - start)];
        var read = 0;
        while (read < buffer.Length && stream.Read(buffer, read, buffer.Length - read) is var n && n > 0)
        {
            read += n;
        }

        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    /// <summary>A prompt as a step ("User: …", or one handed over by another session); null for any other line.</summary>
    private static string? PromptOf(string line) => line.Contains("\"user\"", StringComparison.Ordinal) && StepOf(line) is { } step
        && step.StartsWith("User", StringComparison.Ordinal) ? step : null;

    /// <summary>What the line adds to the digest, one or more lines of it; null for a line that adds nothing.</summary>
    private static string? StepOf(string line)
    {
        if (line.Length == 0 || !(line.Contains("\"user\"", StringComparison.Ordinal) || line.Contains("\"assistant\"", StringComparison.Ordinal)))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)
                || (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True)
                || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content))
            {
                return null;
            }

            return type.GetString() switch
            {
                "user" => UserStep(root, content),
                "assistant" => AssistantStep(content),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? UserStep(JsonElement root, JsonElement content)
    {
        if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        var text = content.ValueKind == JsonValueKind.String ? content.GetString()
            : content.ValueKind == JsonValueKind.Array
                ? string.Join(" ", content.EnumerateArray()
                    .Where(b => b.ValueKind == JsonValueKind.Object && b.TryGetProperty("type", out var t) && t.GetString() == "text"
                        && b.TryGetProperty("text", out var v) && v.ValueKind == JsonValueKind.String)
                    .Select(b => b.GetProperty("text").GetString()))
                : null; // a tool's result is in the chat's own words after it
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (root.TryGetProperty("isCompactSummary", out var compacted) && compacted.ValueKind == JsonValueKind.True)
        {
            return "Earlier, as Claude Code summed it up when it compacted the chat: " + Short(text, MaxStepChars * 3);
        }

        if (text.StartsWith("<command-", StringComparison.Ordinal) || text.StartsWith("<local-command", StringComparison.Ordinal)
            || text.StartsWith("Caveat:", StringComparison.Ordinal))
        {
            return null; // a slash command and its output: no request
        }

        if (text.StartsWith("<task-notification>", StringComparison.Ordinal))
        {
            return "A background task of the chat ended.";
        }

        if (text.StartsWith("[Request interrupted", StringComparison.Ordinal))
        {
            return "The user stopped it.";
        }

        if (CrossSessionMessage.SenderOf(text) is not null)
        {
            return ChatTitle.FromPrompt(text, MaxStepChars) is { } task ? "User (through Raven or another session): " + task : null;
        }

        return "User: " + Short(text, MaxStepChars);
    }

    private static string? AssistantStep(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var steps = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var kind))
            {
                continue;
            }

            if (kind.GetString() == "text" && block.TryGetProperty("text", out var text) && text.GetString() is { Length: > 0 } said
                && said.Trim().Length > 0)
            {
                steps.Add("Claude: " + Short(said.Trim(), MaxStepChars));
            }
            else if (kind.GetString() == "tool_use" && block.TryGetProperty("name", out var name) && name.GetString() is { } tool)
            {
                steps.Add(ToolStep(tool, block.TryGetProperty("input", out var input) ? input : default));
            }
        }

        return steps.Count == 0 ? null : string.Join('\n', steps);
    }

    /// <summary>"Changed a file: src/App.cs", "Ran: npm test", "Used Grep: upload".</summary>
    private static string ToolStep(string tool, JsonElement input)
    {
        string? Field(params string[] names) => input.ValueKind != JsonValueKind.Object ? null
            : names.Select(n => input.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        return tool switch
        {
            "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => "Changed a file: " + (Field("file_path", "notebook_path", "path") ?? "?"),
            "Bash" or "PowerShell" => "Ran: " + Short(Field("command") ?? "?", 200),
            _ => Field("file_path", "path", "pattern", "url", "query", "description", "skill", "subject") is { } about
                ? $"Used {tool}: {Short(about, 200)}" : $"Used {tool}",
        };
    }

    private static string Short(string text, int max)
    {
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= max ? line : line[..(max - 1)].TrimEnd() + "…";
    }
}
