using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeSwitchX.Core;

namespace CodeSwitchX.Ingest.Transcripts;

/// <summary>
/// What a chat last said: the text of the last assistant message of the main chat (no subagent's) in its transcript,
/// read from the end of the file, so a long transcript costs no more than a short one.
/// </summary>
public static partial class TranscriptLastReply
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

            var said = text.ToString();
            return string.IsNullOrWhiteSpace(said) ? null : said;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A reply's sentences on one line (#301): its table rows, rules and code blocks left out, a link's target too, and the marks
    /// of headings, list items, quotes, bold and code taken off; a heading or list item ends as a sentence. A reply ending in a
    /// table gave its last cells ("| Skipped | 580 rows |") to be told.
    /// </summary>
    internal static string Prose(string text)
    {
        var prose = new StringBuilder();
        string? fence = null; // the open code block's fence ("```", "````", "~~~"): only a run as long, alone, closes it
        foreach (var raw in text.Split('\n'))
        {
            var line = Quote().Replace(raw.Trim(), ""); // quoted code or a quoted table is left out as well
            if (fence is not null)
            {
                if (line.Length >= fence.Length && line.All(c => c == fence[0]))
                {
                    fence = null;
                }

                continue;
            }

            if (Fence().Match(line) is { Success: true } open)
            {
                fence = open.Groups[1].Value;
                continue;
            }

            if (line.StartsWith('|') || Rule().IsMatch(line))
            {
                continue;
            }

            var item = Heading().Match(line) is { Success: true } heading ? heading : Bullet().Match(line);
            var bold = BoldLine().IsMatch(line); // "**Summary**" stands as a heading
            line = Link().Replace(line[item.Length..], "$1").Replace("**", "").Replace("`", "").Trim();
            if (line.Length == 0)
            {
                continue;
            }

            prose.Append(line);
            if ((item.Success || bold) && !".!?:;…".Contains(line[^1]))
            {
                prose.Append('.'); // "## Done" and "- Fixed login" are told as sentences, not "Done Fixed login"
            }

            prose.Append(' ');
        }

        return TextCut.OneLine(prose.ToString());
    }

    /// <summary>A code fence opening: three or more backticks or tildes, then an info string with no backtick ("```npm test``` passes" is prose).</summary>
    [GeneratedRegex(@"^(`{3,}|~{3,})[^`]*$")]
    private static partial Regex Fence();

    /// <summary>A rule or setext underline: "---", "***", "___", "===".</summary>
    [GeneratedRegex(@"^([-*_=])(\s*\1){2,}$")]
    private static partial Regex Rule();

    /// <summary>A heading's marks: one to six '#' and a space, so "#301 is merged" keeps its number.</summary>
    [GeneratedRegex(@"^#{1,6}\s+")]
    private static partial Regex Heading();

    /// <summary>A list item's mark: "-", "*", "+", or a number with "." or ")".</summary>
    [GeneratedRegex(@"^([-*+]|\d+[.)])\s+")]
    private static partial Regex Bullet();

    /// <summary>A line all in bold, as a heading is often written.</summary>
    [GeneratedRegex(@"^\*\*[^*]+\*\*:?$")]
    private static partial Regex BoldLine();

    [GeneratedRegex(@"^(>\s?)+")]
    private static partial Regex Quote();

    /// <summary>A markdown link: its words are told, not its target.</summary>
    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

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
