namespace CodeSwitchX.Voice.Tests.Dictation;

using CodeSwitchX.Voice.Dictation;

// Ported from ContentAutomatorX.
public sealed class VocabularyPromptTests
{
    [Fact]
    public void An_empty_list_gives_an_empty_string()
    {
        VocabularyPrompt.Build([]).ShouldBe("");
    }

    [Fact]
    public void Words_are_joined_with_comma_and_space()
    {
        VocabularyPrompt.Build(["ComfyUI", "Civitai", "LoRA"]).ShouldBe("ComfyUI, Civitai, LoRA");
    }

    [Fact]
    public void Blanks_are_dropped_and_words_trimmed()
    {
        VocabularyPrompt.Build([" ComfyUI ", "", "  ", "Civitai"]).ShouldBe("ComfyUI, Civitai");
    }

    // Whisper keeps only the end of a long prompt, so a long list would lose its first words, the ones that matter
    // most. The cap drops from the end instead, whole words only.
    [Fact]
    public void A_long_list_is_cut_from_the_end_at_the_cap_keeping_the_first_words_whole()
    {
        var words = Enumerable.Range(1, 100).Select(i => $"Workspace{i:D3}").ToList();

        var prompt = VocabularyPrompt.Build(words);

        prompt.Length.ShouldBeLessThanOrEqualTo(VocabularyPrompt.MaximumLength);
        prompt.ShouldStartWith("Workspace001, Workspace002");
        var kept = prompt.Split(", ");
        kept.ShouldBe(words.Take(kept.Length));
        (prompt.Length + 2 + words[kept.Length].Length).ShouldBeGreaterThan(VocabularyPrompt.MaximumLength, "the next word did not fit");
    }
}
