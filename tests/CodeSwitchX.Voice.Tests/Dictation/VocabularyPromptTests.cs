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
}
