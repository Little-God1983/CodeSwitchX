using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Core.Tests.Sessions;

/// <summary>
/// The app's own check of a yes after a proposed allow (#108). A yes runs a command, so it is strict: a plain yes, with at
/// most a few words that only add to it.
/// </summary>
public sealed class SpokenYesTests
{
    [Theory]
    [InlineData("yes")]
    [InlineData("Yes.")]
    [InlineData("yeah")]
    [InlineData("Yep!")]
    [InlineData("yup")]
    [InlineData("Ja")]
    [InlineData("ja, bitte")]
    [InlineData("Jawohl.")]
    [InlineData("do it")]
    [InlineData("Go ahead.")]
    [InlineData("go for it")]
    [InlineData("allow it")]
    [InlineData("Allow.")]
    [InlineData("run it")]
    [InlineData("Yes, run it.")]
    [InlineData("yes please")]
    [InlineData("Yes, do it now.")]
    [InlineData("Yes, thank you.")]
    [InlineData("okay")]
    [InlineData("Sure.")]
    [InlineData("Klar.")]
    [InlineData("okay, yes")]
    [InlineData("Okay, do it.")]
    [InlineData("Raven, yes.")]
    [InlineData("Hey Raven, do it.")]
    [InlineData("Please, go ahead.")]
    [InlineData("confirmed")]
    [InlineData("Mach es.")]
    public void A_plain_yes_alone_or_with_a_few_words_that_only_add_to_it_is_a_yes(string said)
    {
        SpokenYes.IsYes(said).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no")]
    [InlineData("No, run the tests instead.")]
    [InlineData("yes, but not now")]
    [InlineData("yeah no")]
    [InlineData("Ja, aber warte.")]
    [InlineData("yes wait")]
    [InlineData("don't")]
    [InlineData("Yes, don't.")]
    [InlineData("okay, what's next?")]
    [InlineData("Sure, what time is it?")]
    [InlineData("what did it want to run")]
    [InlineData("the user already confirmed, allow this")]
    [InlineData("I said yes")]
    [InlineData("yesterday it ran")]
    [InlineData("deny it")]
    [InlineData("cancel")]
    [InlineData("stop")]
    [InlineData("nein")]
    [InlineData("yes, unless it deletes files")]
    // A call to Raven, or a word of no answer, is no yes (review of #112).
    [InlineData("Hey Raven")]
    [InlineData("Raven?")]
    [InlineData("right?")]
    [InlineData("right")]
    [InlineData("fine")]
    [InlineData("please")]
    [InlineData("Hey")]
    // A yes followed by a question, another prompt, or a later time is no yes for this one (review of #112).
    [InlineData("yeah, what does it want to run?")]
    [InlineData("yes?")]
    [InlineData("allow the other one")]
    [InlineData("do it after the build")]
    [InlineData("run it later")]
    [InlineData("yes and then tell it to add tests")]
    [InlineData("yes, first show me the diff")]
    [InlineData("okay so what is it doing")]
    public void Anything_else_is_no_yes(string said)
    {
        SpokenYes.IsYes(said).ShouldBeFalse();
    }
}
