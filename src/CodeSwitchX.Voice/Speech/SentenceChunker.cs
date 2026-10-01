namespace CodeSwitchX.Voice.Speech;

/// <summary>
/// Cuts a reply that streams in as pieces into sentences, each given as soon as it is known to be whole, so it can be
/// spoken while the rest is still coming. A sentence ends at <c>.</c>, <c>!</c> or <c>?</c> (with any closing quotes or
/// brackets) once whitespace follows, and at every line break. A dot inside a word or number ("auth.cs", "3.5"), after
/// a common abbreviation ("e.g.", "(e.g.") or after an item's number ("1.", indented or not) ends nothing. Fenced code
/// blocks are left out. Each piece is looked at once: what was looked at and holds no end is not scanned again.
/// Not thread-safe: one reply, fed in order.
/// </summary>
public sealed class SentenceChunker
{
    private const string Fence = "```";
    private const string Enders = ".!?";
    private const string Closers = "\"')]’”";
    private const string Openers = "\"'([{‘“";

    private static readonly HashSet<string> Abbreviations =
        new(["e.g", "i.e", "vs", "mr", "mrs", "ms", "dr", "cf", "approx"], StringComparer.OrdinalIgnoreCase);

    private char[] _chars = new char[256];
    private int _count;

    /// <summary>The text before this index holds no line break and no end of a sentence: scanning goes on from here.</summary>
    private int _scanned;

    /// <summary>The buffer starts at the start of a line, so it may be a fence.</summary>
    private bool _lineStart = true;
    private bool _inCode;

    /// <summary>Takes the next piece of the reply; returns the sentences it completed, in order.</summary>
    public IReadOnlyList<string> Add(string piece)
    {
        if (_count + piece.Length > _chars.Length)
        {
            Array.Resize(ref _chars, Math.Max(_chars.Length * 2, _count + piece.Length));
        }

        piece.CopyTo(0, _chars, _count, piece.Length);
        _count += piece.Length;
        return Scan(final: false);
    }

    /// <summary>The reply is complete: returns what is left, which ends a sentence whatever its last character.</summary>
    public IReadOnlyList<string> Flush()
    {
        var rest = Scan(final: true);
        _count = 0;
        _scanned = 0;
        _lineStart = true;
        _inCode = false;
        return rest;
    }

    private ReadOnlySpan<char> Text => _chars.AsSpan(0, _count);

    private List<string> Scan(bool final)
    {
        var sentences = new List<string>();
        while (_count > 0)
        {
            if (_inCode || (_lineStart && MayBeFence(final)))
            {
                var newline = NextNewline();
                if (newline < 0 && !final)
                {
                    break; // the line is not whole yet
                }

                var line = newline < 0 ? Text : Text[..newline];
                var isFence = line.TrimStart().StartsWith(Fence, StringComparison.Ordinal);
                Remove(newline < 0 ? _count : newline + 1);
                if (isFence)
                {
                    _inCode = !_inCode;
                }

                continue;
            }

            var end = FindEnd(final, out var consumed);
            if (end < 0)
            {
                break;
            }

            var sentence = Text[..end].Trim().ToString();
            Remove(consumed);
            _lineStart = consumed > end; // the line break itself was consumed
            if (sentence.Length > 0)
            {
                sentences.Add(sentence);
            }
        }

        return sentences;
    }

    private void Remove(int length)
    {
        Array.Copy(_chars, length, _chars, 0, _count - length);
        _count -= length;
        _scanned = 0;
    }

    /// <summary>The first line break from where scanning stopped; -1 while there is none, and the scan moves past it all.</summary>
    private int NextNewline()
    {
        var found = Text[_scanned..].IndexOf('\n');
        if (found < 0)
        {
            _scanned = _count;
            return -1;
        }

        return _scanned + found;
    }

    /// <summary>
    /// The line starting the buffer is, or may still become, a fence: after any spaces, backticks only so far, or a
    /// fence. Only the start of the line is looked at, never the whole buffer.
    /// </summary>
    private bool MayBeFence(bool final)
    {
        var text = Text;
        var start = 0;
        while (start < text.Length && text[start] is ' ' or '\t')
        {
            start++;
        }

        var rest = text[start..];
        if (rest.StartsWith(Fence, StringComparison.Ordinal))
        {
            return true;
        }

        // "``" may still grow into a fence: wait for more, unless nothing more comes.
        var ticks = 0;
        while (ticks < rest.Length && rest[ticks] == '`')
        {
            ticks++;
        }

        return !final && ticks == rest.Length;
    }

    /// <summary>
    /// Where the first whole sentence ends (exclusive), and how much of the buffer it takes, the line break after it
    /// included; -1 while none is whole yet. Starts where the last scan stopped.
    /// </summary>
    private int FindEnd(bool final, out int consumed)
    {
        var text = Text;
        for (var i = _scanned; i < text.Length; i++)
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
                // Whether whitespace follows is not known yet: look again from this mark.
                _scanned = i;
                consumed = text.Length;
                return final ? text.Length : -1;
            }

            if (char.IsWhiteSpace(text[after]) && !(c == '.' && EndsNothing(text, i)))
            {
                consumed = after;
                return after;
            }
        }

        _scanned = text.Length;
        consumed = text.Length;
        return final ? text.Length : -1;
    }

    /// <summary>The dot at <paramref name="dot"/> closes an abbreviation, or an item's number at the start of a line, indented or not.</summary>
    private bool EndsNothing(ReadOnlySpan<char> text, int dot)
    {
        var start = dot;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }

        var word = text[start..dot];
        if (Abbreviations.Contains(word.TrimStart(Openers).ToString()))
        {
            return true;
        }

        return _lineStart && text[..start].IsWhiteSpace() && word.Length > 0 && IsDigits(word);
    }

    private static bool IsDigits(ReadOnlySpan<char> word)
    {
        foreach (var c in word)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
