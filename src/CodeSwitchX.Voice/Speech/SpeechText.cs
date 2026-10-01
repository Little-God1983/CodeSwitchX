using System.Text;
using System.Text.RegularExpressions;

namespace CodeSwitchX.Voice.Speech;

/// <summary>Text made fit to be spoken. Ported from RAIVEN's <c>SpeechText</c>; links, addresses and list markers are new.</summary>
public static partial class SpeechText
{
    /// <summary>Sentence punctuation a voice reads naturally; everything else but letters, digits and whitespace is dropped.</summary>
    private const string AllowedPunctuation = ".,!?;:'\"()-";

    /// <summary>
    /// Strips the emojis, markdown and stray symbols a voice mispronounces while keeping real words and sentence
    /// punctuation. A keep-list, so emojis (multi-code-point ones too) and unknown symbols fall away without enumerating
    /// them; letters of any language, German umlauts included, stay. A markdown link keeps its words, a bare address
    /// and a list marker go, smart quotes and ellipses become plain ones, and a couple of signs are spoken as words.
    /// </summary>
    public static string CleanForSpeech(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = MarkdownLink().Replace(text, "$1");
        text = BareAddress().Replace(text, " ");
        text = ListMarker().Replace(text, "");

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '‘' or '’' or '‚' or '‛': sb.Append('\''); break;
                case '“' or '”' or '„' or '‟': sb.Append('"'); break;
                case '…': sb.Append("..."); break;
                case '&': sb.Append(" and "); break;
                case '%': sb.Append(" percent "); break;
                default:
                    // An emoji, markdown or another symbol leaves a gap, collapsed below.
                    sb.Append(char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) || AllowedPunctuation.Contains(ch) ? ch : ' ');
                    break;
            }
        }

        // Collapses the gaps left by stripped symbols (and any original run of whitespace), and trims.
        var cleaned = string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return SpaceBeforePunctuation().Replace(cleaned, "$1");
    }

    /// <summary>
    /// Ends a sentence with a full stop when it has no end of its own ("Let me check", "Workspaces:"): Qwen3-TTS often
    /// misses the end of a sentence without one, and babbles on until it is stopped. Closing quotes and brackets stay last.
    /// </summary>
    public static string EndSentence(string sentence)
    {
        var body = sentence.TrimEnd();
        var closers = body.Length - body.TrimEnd('"', '\'', ')').Length;
        var core = body[..^closers].TrimEnd(',', ';', ':', '-', ' ');
        if (core.Length == 0)
        {
            return body;
        }

        return (".!?".Contains(core[^1]) || core.EndsWith("...", StringComparison.Ordinal) ? core : core + ".") + body[^closers..];
    }

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"\b(?:https?|ftp)://\S+|\bwww\.\S+", RegexOptions.IgnoreCase)]
    private static partial Regex BareAddress();

    [GeneratedRegex(@"^\s*[-*+]\s+", RegexOptions.Multiline)]
    private static partial Regex ListMarker();

    /// <summary>"at ." after an address went: the gap before the punctuation goes too.</summary>
    [GeneratedRegex(@" ([.,!?;:])(?=\s|$)")]
    private static partial Regex SpaceBeforePunctuation();
}
