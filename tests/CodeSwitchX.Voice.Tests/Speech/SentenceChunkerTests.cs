namespace CodeSwitchX.Voice.Tests.Speech;

using CodeSwitchX.Voice.Speech;

public sealed class SentenceChunkerTests
{
    private static List<string> Chunk(params string[] deltas)
    {
        var chunker = new SentenceChunker();
        var sentences = new List<string>();
        foreach (var delta in deltas)
        {
            sentences.AddRange(chunker.Add(delta));
        }

        sentences.AddRange(chunker.Flush());
        return sentences;
    }

    [Fact]
    public void A_sentence_is_given_once_the_text_after_it_has_begun()
    {
        var chunker = new SentenceChunker();
        chunker.Add("Two chats are working.").ShouldBeEmpty();
        chunker.Add(" One").ShouldBe(["Two chats are working."]);
        chunker.Flush().ShouldBe(["One"]);
    }

    [Fact]
    public void Deltas_cut_anywhere_give_the_same_sentences()
    {
        Chunk("Th", "e build fai", "led! Want me to r", "etry? Ok", "ay.")
            .ShouldBe(["The build failed!", "Want me to retry?", "Okay."]);
    }

    [Fact]
    public void A_dot_inside_a_word_or_number_ends_nothing()
    {
        Chunk("Open auth.cs in version 3.5 now. Done.").ShouldBe(["Open auth.cs in version 3.5 now.", "Done."]);
    }

    [Fact]
    public void Common_abbreviations_end_nothing()
    {
        Chunk("Some chats, e.g. the API one, wait. Next.").ShouldBe(["Some chats, e.g. the API one, wait.", "Next."]);
    }

    [Fact]
    public void A_closing_quote_or_bracket_stays_with_its_sentence()
    {
        Chunk("It said \"done.\" Then (it stopped.) Fine.").ShouldBe(["It said \"done.\"", "Then (it stopped.)", "Fine."]);
    }

    [Fact]
    public void A_line_break_ends_a_sentence_and_blank_lines_give_nothing()
    {
        Chunk("Workspaces:\n\n- Frontend\n- API").ShouldBe(["Workspaces:", "- Frontend", "- API"]);
    }

    [Fact]
    public void A_numbered_item_is_not_cut_after_its_number()
    {
        Chunk("1. Frontend\n2. API").ShouldBe(["1. Frontend", "2. API"]);
    }

    [Fact]
    public void A_fenced_code_block_is_left_out()
    {
        Chunk("Run this:\n``", "`bash\ndotnet test.\n``", "`\nThen wait.").ShouldBe(["Run this:", "Then wait."]);
    }

    [Fact]
    public void An_unclosed_code_block_is_left_out_to_the_end()
    {
        Chunk("Here.\n```\nvar x = 1;").ShouldBe(["Here."]);
    }

    [Fact]
    public void A_flush_gives_the_rest_once()
    {
        var chunker = new SentenceChunker();
        chunker.Add("No dot at the end");
        chunker.Flush().ShouldBe(["No dot at the end"]);
        chunker.Flush().ShouldBeEmpty();
    }
}
