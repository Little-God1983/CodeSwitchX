namespace CodeSwitchX.Voice.Tests.Dictation;

using CodeSwitchX.Voice.Dictation;

// Ported from ContentAutomatorX.
public sealed class TranscriptCorrectorTests
{
    [Fact]
    public void An_empty_list_returns_the_input_unchanged()
    {
        TranscriptCorrector.Apply("hello there", []).ShouldBe("hello there");
    }

    [Fact]
    public void Replacement_ignores_case()
    {
        var result = TranscriptCorrector.Apply("I opened Comfy Your Eye today",
            [new Correction("comfy your eye", "ComfyUI")]);
        result.ShouldBe("I opened ComfyUI today");
    }

    [Fact]
    public void The_longest_phrase_wins()
    {
        var result = TranscriptCorrector.Apply("comfy your eye",
            [new Correction("eye", "I"), new Correction("comfy your eye", "ComfyUI")]);
        result.ShouldBe("ComfyUI");
    }

    [Fact]
    public void The_longest_trimmed_phrase_wins()
    {
        var result = TranscriptCorrector.Apply("some fine art",
            [new Correction("    art    ", "ART"), new Correction("fine art", "Fine Art")]);
        result.ShouldBe("some Fine Art");
    }

    [Fact]
    public void Only_whole_words_are_matched()
    {
        var result = TranscriptCorrector.Apply("part of the art", [new Correction("art", "ART")]);
        result.ShouldBe("part of the ART");
    }

    [Fact]
    public void Whitespace_in_the_heard_phrase_matches_any_run_of_whitespace()
    {
        var result = TranscriptCorrector.Apply("stable  diffusion is fun",
            [new Correction("stable diffusion", "Stable Diffusion")]);
        result.ShouldBe("Stable Diffusion is fun");
    }

    [Fact]
    public void Every_occurrence_is_replaced()
    {
        var result = TranscriptCorrector.Apply("civet ai and civet ai again",
            [new Correction("civet ai", "Civitai")]);
        result.ShouldBe("Civitai and Civitai again");
    }

    [Fact]
    public void Regex_characters_in_the_heard_phrase_are_literal()
    {
        var result = TranscriptCorrector.Apply("what is c++ (really)", [new Correction("c++ (really)", "C++")]);
        result.ShouldBe("what is C++");
    }
}
