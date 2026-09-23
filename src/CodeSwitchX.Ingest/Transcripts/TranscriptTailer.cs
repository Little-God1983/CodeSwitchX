using System.Text;

namespace CodeSwitchX.Ingest.Transcripts;

/// <param name="Lines">Complete lines read in this pass.</param>
/// <param name="NewOffset">Byte offset to resume from.</param>
/// <param name="Truncated">The file shrank below the requested offset; reading restarted at zero.</param>
/// <param name="HasMore">The pass stopped at the byte cap and unread bytes remain.</param>
public readonly record struct TailResult(IReadOnlyList<string> Lines, long NewOffset, bool Truncated, bool HasMore = false);

/// <summary>
/// Reads whole lines appended after a byte offset, at most <c>maxBytes</c> per pass so a multi-hundred-megabyte
/// transcript never lands in memory at once; a trailing line without a newline is left for the next call.
/// </summary>
public static class TranscriptTailer
{
    public const int DefaultMaxBytes = 8 * 1024 * 1024;

    public static TailResult ReadNewLines(string path, long fromOffset, int maxBytes = DefaultMaxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        var truncated = false;
        if (fromOffset > stream.Length)
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
            // Either a partial trailing line (wait for more) or a single line longer than the cap (skip it).
            return capped
                ? new TailResult([], fromOffset + read, truncated, HasMore: true)
                : new TailResult([], fromOffset, truncated);
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
}
