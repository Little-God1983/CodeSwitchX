using System.Text;
using System.Text.Json;
using CodeSwitchX.Core;

namespace CodeSwitchX.Ingest.Transcripts;

/// <summary>
/// What a chat last said: the text of the last assistant message of the main chat (no subagent's) in its transcript,
/// read from the end of the file, so a long transcript costs no more than a short one.
/// </summary>
public static class TranscriptLastReply
{
    /// <summary>How much of the file's end is read: the last reply and the tool calls after it fit easily.</summary>
    public const int TailBytes = 256 * 1024;

    /// <summary>
    /// The end of the chat's last reply as prose (<see cref="Prose"/>), at most <paramref name="maxChars"/> long and cut at a word
    /// ("…" before it); null when none is found, it has no prose, or the file cannot be read.
    /// </summary>
    public static string? Read(string? path, int maxChars = 300)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string tail;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            tail = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var lines = tail.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (TextOf(lines[i]) is { } text)
            {
                // A reply that is all table or code says nothing: an earlier one ("Now the tests.") would be older news.
                var said = Prose(text);
                return said.Length > 0 ? Shorten(said, maxChars) : null;
            }
        }

        return null;
    }

    /// <summary>The text of a main-chat assistant line; null for every other line, a partial first one too.</summary>
    private static string? TextOf(string line)
    {
        if (!line.Contains("\"assistant\"", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.GetString() != "assistant"
                || (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True)
                || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var text = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var kind) && kind.GetString() == "text"
                    && block.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
                {
                    text.Append(value.GetString()).Append('\n');
                }
            }

            return string.IsNullOrWhiteSpace(text.ToString()) ? null : text.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A reply's sentences on one line (#301): its table rows and code blocks left out, and the marks of headings, list items,
    /// quotes, bold and code taken off. A reply ending in a table gave its last cells ("| Skipped | 580 rows |") to be told.
    /// </summary>
    internal static string Prose(string text)
    {
        var prose = new StringBuilder();
        var inCode = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }

            if (inCode || line.StartsWith('|'))
            {
                continue;
            }

            line = line.TrimStart('#', '>').TrimStart();
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("+ ", StringComparison.Ordinal))
            {
                line = line[2..];
            }

            prose.Append(line.Replace("**", "").Replace("`", "")).Append(' ');
        }

        return TextCut.OneLine(prose.ToString());
    }

    private static string Shorten(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var cut = text[^maxChars..];
        var space = cut.IndexOf(' ');
        return "…" + (space >= 0 && space < cut.Length - 1 ? cut[(space + 1)..] : cut);
    }
}
