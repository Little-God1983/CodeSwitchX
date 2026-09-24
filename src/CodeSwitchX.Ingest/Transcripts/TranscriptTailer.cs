using System.Text;

namespace CodeSwitchX.Ingest.Transcripts;

/// <param name="Lines">Complete lines read in this pass.</param>
/// <param name="NewOffset">Byte offset to resume from.</param>
/// <param name="Truncated">The file no longer fits the requested offset (it shrank below it, or the offset no longer follows a
/// line end because the file was rewritten); reading restarted at zero.</param>
/// <param name="HasMore">The pass stopped at the byte cap and unread bytes remain.</param>
public readonly record struct TailResult(IReadOnlyList<string> Lines, long NewOffset, bool Truncated, bool HasMore = false);

/// <summary>
/// Reads whole lines appended after a byte offset, at most <c>maxBytes</c> per pass so a multi-hundred-megabyte
/// transcript never lands in memory at once; a trailing line without a newline is left for the next call, and a line longer
/// than the cap is skipped whole.
/// </summary>
public static class TranscriptTailer
{
    public const int DefaultMaxBytes = 8 * 1024 * 1024;

    public static TailResult ReadNewLines(string path, long fromOffset, int maxBytes = DefaultMaxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        // Claude Code does not only append: it cuts a retracted message out of the file and writes back what followed.
        var truncated = false;
        if (fromOffset > stream.Length || (fromOffset > 0 && ByteBefore(stream, fromOffset) != '\n'))
        {
            truncated = true;
            fromOffset = 0;
        }

        if (fromOffset == stream.Length)
        {
            return new TailResult([], fromOffset, truncated);
        }

        stream.Position = fromOffset;
        var remaining = stream.Length - fromOffset;
        var buffer = new byte[Math.Min(remaining, maxBytes)];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        var capped = read < remaining;
        var lastNewline = read == 0 ? -1 : Array.LastIndexOf(buffer, (byte)'\n', read - 1);
        if (lastNewline < 0)
        {
            // Either a partial trailing line (wait for more) or a single line longer than the cap. That one is skipped up to
            // its end, so the next pass starts after a line end like every other.
            var lineEnd = capped ? LineEndAfter(stream, fromOffset + read) : -1;
            return lineEnd < 0
                ? new TailResult([], fromOffset, truncated)
                : new TailResult([], lineEnd, truncated, HasMore: lineEnd < stream.Length);
        }

        // Skip a UTF-8 byte-order mark at the start of the file; offsets stay byte-accurate.
        var start = fromOffset == 0 && read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
        var text = Encoding.UTF8.GetString(buffer, start, lastNewline + 1 - start);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();
        var newOffset = fromOffset + lastNewline + 1;
        return new TailResult(lines, newOffset, truncated, HasMore: newOffset < stream.Length && capped);
    }

    private static int ByteBefore(FileStream stream, long offset)
    {
        stream.Position = offset - 1;
        return stream.ReadByte();
    }

    /// <returns>The offset just after the first newline at or after <paramref name="from"/>, or -1 when there is none yet.</returns>
    private static long LineEndAfter(FileStream stream, long from)
    {
        stream.Position = from;
        var buffer = new byte[64 * 1024];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var i = Array.IndexOf(buffer, (byte)'\n', 0, n);
            if (i >= 0)
            {
                return stream.Position - n + i + 1;
            }
        }

        return -1;
    }
}
