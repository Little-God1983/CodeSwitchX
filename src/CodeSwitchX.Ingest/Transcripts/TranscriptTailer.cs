using System.Text;

namespace CodeSwitchX.Ingest.Transcripts;

public readonly record struct TailResult(IReadOnlyList<string> Lines, long NewOffset, bool Truncated);

/// <summary>Reads whole lines appended after a byte offset; a trailing line without a newline is left for the next call.</summary>
public static class TranscriptTailer
{
    public static TailResult ReadNewLines(string path, long fromOffset)
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
        var buffer = new byte[stream.Length - fromOffset];
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

        var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
        if (lastNewline < 0)
        {
            return new TailResult([], fromOffset, truncated);
        }

        // Skip a UTF-8 byte-order mark at the start of the file; offsets stay byte-accurate.
        var start = fromOffset == 0 && read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
        var text = Encoding.UTF8.GetString(buffer, start, lastNewline + 1 - start);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();
        return new TailResult(lines, fromOffset + lastNewline + 1, truncated);
    }
}
