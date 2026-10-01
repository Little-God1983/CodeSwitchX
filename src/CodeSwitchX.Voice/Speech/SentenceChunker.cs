using System.Text;

namespace CodeSwitchX.Voice.Speech;

/// <summary>
/// Cuts a reply that streams in as pieces into sentences, each given as soon as it is known to be whole, so it can be
/// spoken while the rest is still coming. A sentence ends at <c>.</c>, <c>!</c> or <c>?</c> (with any closing quotes or
/// brackets) once whitespace follows, and at every line break. A dot inside a word or number ("auth.cs", "3.5"), after
/// a common abbreviation ("e.g.") or after an item's number ("1.") ends nothing. Fenced code blocks are left out.
/// Not thread-safe: one reply, fed in order.
/// </summary>
public sealed class SentenceChunker
{
    private const string Fence = "```";
    private const string Enders = ".!?";
    private const string Closers = "\"')]’”";

    private static readonly HashSet<string> Abbreviations =
        new(["e.g", "i.e", "vs", "mr", "mrs", "ms", "dr", "cf", "approx"], StringComparer.OrdinalIgnoreCase);

    private readonly StringBuilder _buffer = new();

    /// <summary>The buffer starts at the start of a line, so it may be a fence.</summary>
    private bool _lineStart = true;
    private bool _inCode;

    /// <summary>Takes the next piece of the reply; returns the sentences it completed, in order.</summary>
    public IReadOnlyList<string> Add(string piece)
    {
        _buffer.Append(piece);
        return Scan(final: false);
    }

    /// <summary>The reply is complete: returns what is left, which ends a sentence whatever its last character.</summary>
    public IReadOnlyList<string> Flush()
    {
        var rest = Scan(final: true);
        _buffer.Clear();
        _lineStart = true;
        _inCode = false;
        return rest;
    }

    private List<string> Scan(bool final)
    {
        var sentences = new List<string>();
        while (_buffer.Length > 0)
        {
            var text = _buffer.ToString();
            var newline = text.IndexOf('\n');
            if (_inCode || (_lineStart && MayBeFence(text, newline, final)))
            {
                if (newline < 0 && !final)
                {
                    break; // the line is not whole yet
                }

                var line = newline < 0 ? text : text[..newline];
                _buffer.Remove(0, newline < 0 ? text.Length : newline + 1);
                if (line.TrimStart().StartsWith(Fence, StringComparison.Ordinal))
                {
                    _inCode = !_inCode;
                }

                continue;
            }

            var end = FindEnd(text, final, out var consumed);
            if (end < 0)
            {
                break;
            }

            _buffer.Remove(0, consumed);
            _lineStart = consumed > end; // the line break itself was consumed
            if (text[..end].Trim() is { Length: > 0 } sentence)
            {
                sentences.Add(sentence);
            }
        }

        return sentences;
    }

    /// <summary>The line starting the buffer is, or may still become, a fence (only spaces and backticks so far).</summary>
    private static bool MayBeFence(string text, int newline, bool final)
    {
        var line = (newline < 0 ? text : text[..newline]).TrimStart(' ', '\t');
        if (line.StartsWith(Fence, StringComparison.Ordinal))
        {
            return true;
        }

        // "``" may still grow into a fence: wait for more, unless nothing more comes.
        return !final && newline < 0 && Fence.StartsWith(line, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where the first whole sentence ends in <paramref name="text"/> (exclusive), and how much of the text it takes,
    /// the line break after it included; -1 while none is whole yet.
    /// </summary>
    private int FindEnd(string text, bool final, out int consumed)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n')
            {
                consumed = i + 1;
                return i;
            }

            if (!Enders.Contains(c))
            {
                continue;
            }

            var after = i + 1;
            while (after < text.Length && Closers.Contains(text[after]))
            {
                after++;
            }

            if (after == text.Length)
            {
                // Whether whitespace follows is not known yet.
                break;
            }

            if (char.IsWhiteSpace(text[after]) && !(c == '.' && EndsNothing(text, i)))
            {
                consumed = after;
                return after;
            }
        }

        consumed = text.Length;
        return final ? text.Length : -1;
    }

    /// <summary>The dot at <paramref name="dot"/> closes an abbreviation, or an item's number at the start of a line, indented or not.</summary>
    private bool EndsNothing(string text, int dot)
    {
        var start = dot;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }

        var word = text[start..dot];
        if (Abbreviations.Contains(word))
        {
            return true;
        }

        return _lineStart && string.IsNullOrWhiteSpace(text[..start]) && word.Length > 0 && word.All(char.IsAsciiDigit);
    }
}
