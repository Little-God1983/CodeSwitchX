using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Core.Tests.Sessions;

/// <summary>The app's own check of a yes after a proposed allow (#108): plain, or with words that do not contradict it.</summary>
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
    [InlineData("okay")]
    [InlineData("Sure.")]
    [InlineData("okay, yes")]
    [InlineData("Okay, do it.")]
    [InlineData("Raven, yes.")]
    [InlineData("yes and then tell it to add tests")]
    [InlineData("confirmed")]
    [InlineData("Mach es.")]
    public void A_plain_yes_alone_or_with_words_that_add_to_it_is_a_yes(string said)
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
    public void Anything_else_is_no_yes(string said)
    {
        SpokenYes.IsYes(said).ShouldBeFalse();
    }
}
