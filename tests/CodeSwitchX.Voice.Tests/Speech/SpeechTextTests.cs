namespace CodeSwitchX.Voice.Tests.Speech;

using CodeSwitchX.Voice.Speech;

// Ported from RAIVEN's SpeechTextTests; links and addresses are new.
public sealed class SpeechTextTests
{
    [Fact]
    public void Plain_text_is_unchanged()
    {
        SpeechText.CleanForSpeech("I fixed the bug and all tests pass.").ShouldBe("I fixed the bug and all tests pass.");
    }

    [Fact]
    public void Emojis_are_removed()
    {
        SpeechText.CleanForSpeech("All tests pass ✅🎉").ShouldBe("All tests pass");
    }

    [Fact]
    public void Markdown_and_code_decoration_are_stripped()
    {
        SpeechText.CleanForSpeech("**Fixed** the `login` bug in `auth.cs`").ShouldBe("Fixed the login bug in auth.cs");
    }

    [Fact]
    public void Assorted_symbols_are_stripped()
    {
        SpeechText.CleanForSpeech("a | b < c >").ShouldBe("a b c");
    }

    [Fact]
    public void German_letters_and_punctuation_stay()
    {
        SpeechText.CleanForSpeech("Grüße, alles klar!").ShouldBe("Grüße, alles klar!");
    }

    [Fact]
    public void Smart_quotes_become_plain_ones()
    {
        SpeechText.CleanForSpeech("“don’t”").ShouldBe("\"don't\"");
    }

    [Fact]
    public void An_ellipsis_becomes_three_dots()
    {
        SpeechText.CleanForSpeech("Where was I…").ShouldBe("Where was I...");
    }

    [Fact]
    public void Ampersand_and_percent_are_spoken_as_words()
    {
        SpeechText.CleanForSpeech("cats & dogs, 50% done").ShouldBe("cats and dogs, 50 percent done");
    }

    [Fact]
    public void Whitespace_is_collapsed_and_trimmed()
    {
        SpeechText.CleanForSpeech("  hello   world  ").ShouldBe("hello world");
    }

    [Fact]
    public void Empty_or_blank_text_gives_empty()
    {
        SpeechText.CleanForSpeech("").ShouldBe("");
        SpeechText.CleanForSpeech("   ").ShouldBe("");
    }

    [Fact]
    public void A_markdown_link_keeps_its_words_only()
    {
        SpeechText.CleanForSpeech("See [the pull request](https://github.com/x/y/pull/81) for it.")
            .ShouldBe("See the pull request for it.");
    }

    [Theory]
    [InlineData("- Frontend")]
    [InlineData("* Frontend")]
    [InlineData("  + Frontend")]
    public void A_list_marker_is_left_out(string item)
    {
        SpeechText.CleanForSpeech(item).ShouldBe("Frontend");
    }

    [Fact]
    public void A_dash_between_words_stays()
    {
        SpeechText.CleanForSpeech("Frontend - two chats").ShouldBe("Frontend - two chats");
    }

    [Fact]
    public void A_bare_address_is_left_out()
    {
        SpeechText.CleanForSpeech("It is at https://example.com/a?b=c now.").ShouldBe("It is at now.");
    }
}
