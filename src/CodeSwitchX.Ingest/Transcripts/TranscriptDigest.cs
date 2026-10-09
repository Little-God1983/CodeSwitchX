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

    /// <summary>
    /// How much of the file's start is looked through for the first prompt, line by line: a prompt with a pasted image
    /// carries it in its own line. Counted between lines: one line longer than this is still read whole, once.
    /// </summary>
    public const int HeadChars = 4 * 1024 * 1024;

    /// <summary>A file this long or shorter is read whole: its start and end would overlap.</summary>
    internal const int WholeBytes = TailBytes + 512 * 1024;

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

        string tail;
        string? first;
        bool whole;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            whole = stream.Length <= WholeBytes;
            tail = ReadAt(stream, whole ? 0 : stream.Length - TailBytes, whole ? (int)stream.Length : TailBytes);
            first = whole ? tail.Split('\n').Select(PromptOf).OfType<string>().FirstOrDefault() : FirstPromptIn(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // A cut line at the edge of the end read does not parse, and is passed over.
        var steps = tail.Split('\n').Select(StepOf).OfType<string>().ToList();
        return steps.Count == 0 && first is null ? null : Compose(first, steps, maxChars, whole);
    }

    /// <summary>
    /// The steps between <paramref name="from"/> and <paramref name="to"/> only (#237), in short, at most about
    /// <paramref name="maxChars"/>: the latest that fit, with the first prompt among them when they do not all fit. The
    /// whole file is read, line by line. Null when no step falls in that time, or the file cannot be read.
    /// </summary>
    public static string? ReadBetween(string? path, DateTimeOffset from, DateTimeOffset to, int maxChars = 4_000, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var steps = new List<string>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = 0;
            while (reader.ReadLine() is { } line)
            {
                if (++lines % 1000 == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                if (TimeOf(line) is not { } at)
                {
                    continue;
                }

                // Lines come in the order they were written: well past the end, the rest of a long conversation is later still.
                if (at > to + PastEnd)
                {
                    break;
                }

                if (at >= from && at <= to && StepOf(line) is { } step)
                {
                    steps.Add(step);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var first = steps.FirstOrDefault(s => s.StartsWith("User: ", StringComparison.Ordinal) || s.StartsWith("Asked through", StringComparison.Ordinal));
        return steps.Count == 0 ? null : Compose(first, steps, maxChars, complete: true);
    }

    /// <summary>How far past the end a line may be written and the reading still go on: a sub-agent's lines come a little out of order.</summary>
    internal static readonly TimeSpan PastEnd = TimeSpan.FromMinutes(30);

    /// <summary>The time a line was written ("timestamp"); null for a line without one.</summary>
    private static DateTimeOffset? TimeOf(string line)
    {
        const string Key = "\"timestamp\":\"";
        var at = line.IndexOf(Key, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        at += Key.Length;
        var end = line.IndexOf('"', at);
        return end > at && DateTimeOffset.TryParse(line.AsSpan(at, end - at), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var time) ? time : null;
    }

    /// <summary>
    /// The first prompt and the latest steps that fit in <paramref name="maxChars"/>, oldest first, with a note where steps
    /// were left out. <paramref name="complete"/>: the steps are all there are, from the first on.
    /// </summary>
    private static string Compose(string? first, List<string> steps, int maxChars, bool complete)
    {
        var whole = complete;
        var kept = new List<string>();
        var size = first?.Length ?? 0;
        for (var i = steps.Count - 1; i >= 0 && size + steps[i].Length <= maxChars; i--)
        {
            kept.Insert(0, steps[i]);
            size += steps[i].Length + 1;
        }

        // The first prompt says what the chat is for: it stays when the steps after it are too many to keep.
        // A first prompt that is among the steps kept (in a big file, after much that is no prompt) is told once, there.
        var told = first is not null && kept.Contains(first);
        var all = kept.Count == steps.Count && (whole || told);
        var text = new StringBuilder();
        if (!all && first is not null && !told)
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

    /// <summary>The first prompt in the file's first <see cref="HeadChars"/>, line by line; null for none.</summary>
    private static string? FirstPromptIn(FileStream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 64 * 1024, leaveOpen: true);
        long read = 0;
        while (read < HeadChars && reader.ReadLine() is { } line)
        {
            read += line.Length + 1;
            if (PromptOf(line) is { } prompt)
            {
                return prompt;
            }
        }

        return null;
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
        && (step.StartsWith("User: ", StringComparison.Ordinal) || step.StartsWith("Asked through", StringComparison.Ordinal)) ? step : null;

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
                    .Select(b => b.GetProperty("text").GetString()!)
                    .Where(t => !Aside(t))) // VS Code's open file and selection go along as blocks of their own
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
            || text.StartsWith("Caveat:", StringComparison.Ordinal) || Aside(text))
        {
            return null; // a slash command and its output, or what goes along with a prompt: no request
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
            return ChatTitle.FromPrompt(text, MaxStepChars) is { } task ? "Asked through Raven or another chat: " + task : null;
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

    /// <summary>What goes along with a prompt, and is none: a reminder, VS Code's open file or selection, a shell command typed with "!" and its output.</summary>
    private static bool Aside(string text)
    {
        var start = text.TrimStart();
        return start.StartsWith("<system-reminder>", StringComparison.Ordinal) || start.StartsWith("<ide_", StringComparison.Ordinal)
            || start.StartsWith("<bash-", StringComparison.Ordinal);
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
            // How a chat Raven started answers it: what it says there is its answer.
            "SendMessage" => "Answered through a message: " + Short(Field("message") ?? "?", MaxStepChars),
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
