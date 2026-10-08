using CodeSwitchX.Core.Yard;

namespace CodeSwitchX.Core.Tests.Yard;

public class CommandWordTests
{
    [Theory]
    [InlineData("Raven, what's waiting?", "What's waiting?")]
    [InlineData("raven what is chat three doing", "What is chat three doing")]
    [InlineData("Hey Raven, chat three.", "Chat three.")]
    [InlineData("Okay, Raven: open StoryForgeX", "Open StoryForgeX")]
    [InlineData("Raven, yes.", "Yes.")]
    [InlineData("Ravin, ja bitte", "Ja bitte")]
    [InlineData("Rayven. Stop it.", "Stop it.")]
    [InlineData("Raben, was wartet auf mich?", "Was wartet auf mich?")]
    [InlineData("  \"Raven - status", "Status")]
    public void A_turn_that_starts_with_the_word_is_for_Raven_without_it(string said, string rest)
    {
        CommandWord.TryStrip(said, out var words).ShouldBeTrue();
        words.ShouldBe(rest);
    }

    [Theory]
    [InlineData("Raven.")]
    [InlineData("Hey Raven!")]
    [InlineData("raven?")]
    public void The_word_alone_leaves_nothing(string said)
    {
        CommandWord.TryStrip(said, out var words).ShouldBeTrue();
        words.ShouldBe("");
    }

    [Theory]
    [InlineData("At least someone's happy I'm home.")]
    [InlineData("Wohnzimmer 100%.")]
    [InlineData("Ravens are clever birds.")]
    [InlineData("I saw a raven today.")]
    [InlineData("Yes.")]
    [InlineData("chat three")]
    [InlineData("Hey, how are you?")]
    [InlineData("")]
    [InlineData(null)]
    public void Any_other_turn_is_not(string? said)
    {
        CommandWord.TryStrip(said, out var words).ShouldBeFalse();
        words.ShouldBe("");
    }
}
