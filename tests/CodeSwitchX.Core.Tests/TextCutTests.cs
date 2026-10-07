namespace CodeSwitchX.Core.Tests;

/// <summary>One cut for every line, button and sentence that shortens text (review round 2 of #112).</summary>
public sealed class TextCutTests
{
    [Fact]
    public void Text_that_fits_is_kept_as_it_is()
    {
        TextCut.Cut("npm test", 8).ShouldBe("npm test");
    }

    [Fact]
    public void Longer_text_keeps_one_character_fewer_than_the_limit_and_takes_the_suffix()
    {
        TextCut.Cut("abcdefghij", 6).ShouldBe("abcde…");
        TextCut.Cut("abcd efghij", 6, " (more)").ShouldBe("abcd (more)", "the kept part is trimmed");
    }

    [Fact]
    public void A_cut_never_splits_an_emoji()
    {
        TextCut.Cut("abcd😀efgh", 6).ShouldBe("abcd…");
        TextCut.Cut("abc😀defgh", 6).ShouldBe("abc😀…");
    }

    [Fact]
    public void One_line_makes_every_run_of_white_space_one_space()
    {
        TextCut.OneLine("  a\n\n b\tc  ").ShouldBe("a b c");
        TextCut.OneLine(null).ShouldBe("");
    }
}
