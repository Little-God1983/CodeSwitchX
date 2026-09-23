using System.Text;
using CodeSwitchX.Ingest.Transcripts;

namespace CodeSwitchX.Ingest.Tests.Transcripts;

public class TranscriptTailerTests : IDisposable
{
    // Claude Code writes plain UTF-8 without a byte-order mark; Utf8 would add one and shift every offset.
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly string _file = Path.Combine(Path.GetTempPath(), "csx-tail-" + Guid.NewGuid().ToString("N") + ".jsonl");

    public void Dispose() => File.Delete(_file);

    [Fact]
    public void Reads_complete_lines_and_leaves_a_partial_trailing_line_for_later()
    {
        File.WriteAllText(_file, "{\"a\":1}\n{\"b\":2}\n{\"partial\":", Utf8);

        var first = TranscriptTailer.ReadNewLines(_file, 0);

        first.Lines.ShouldBe(["{\"a\":1}", "{\"b\":2}"]);
        first.NewOffset.ShouldBe(Utf8.GetByteCount("{\"a\":1}\n{\"b\":2}\n"));
        first.Truncated.ShouldBeFalse();

        File.AppendAllText(_file, "3}\r\n", Utf8);
        var second = TranscriptTailer.ReadNewLines(_file, first.NewOffset);

        second.Lines.ShouldBe(["{\"partial\":3}"]);
        second.NewOffset.ShouldBe(new FileInfo(_file).Length);
    }

    [Fact]
    public void Offset_beyond_the_file_length_restarts_from_zero_and_reports_truncation()
    {
        File.WriteAllText(_file, "{\"a\":1}\n", Utf8);

        var result = TranscriptTailer.ReadNewLines(_file, 5000);

        result.Truncated.ShouldBeTrue();
        result.Lines.ShouldBe(["{\"a\":1}"]);
        result.NewOffset.ShouldBe(8);
    }

    [Fact]
    public void Nothing_new_returns_no_lines_and_the_same_offset()
    {
        File.WriteAllText(_file, "{\"a\":1}\n", Utf8);

        var result = TranscriptTailer.ReadNewLines(_file, 8);

        result.Lines.ShouldBeEmpty();
        result.NewOffset.ShouldBe(8);
    }

    [Fact]
    public void A_file_locked_for_writing_by_another_process_can_still_be_read()
    {
        using var writer = new FileStream(_file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        writer.Write(Utf8.GetBytes("{\"a\":1}\n"));
        writer.Flush();

        TranscriptTailer.ReadNewLines(_file, 0).Lines.ShouldBe(["{\"a\":1}"]);
    }

    [Fact]
    public void Reads_at_most_the_byte_cap_per_pass_and_continues_on_the_next()
    {
        File.WriteAllText(_file, "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n", Utf8);

        var first = TranscriptTailer.ReadNewLines(_file, 0, maxBytes: 20);
        first.Lines.ShouldBe(["{\"a\":1}", "{\"b\":2}"]);
        first.NewOffset.ShouldBe(16);
        first.HasMore.ShouldBeTrue();

        var second = TranscriptTailer.ReadNewLines(_file, first.NewOffset, maxBytes: 20);
        second.Lines.ShouldBe(["{\"c\":3}"]);
        second.NewOffset.ShouldBe(24);
        second.HasMore.ShouldBeFalse();
    }
}
