using CodeSwitchX.Core.Yard;

namespace CodeSwitchX.Core.Tests.Yard;

public class SpokenNextQuestionTests
{
    [Theory]
    [InlineData("next question")]
    [InlineData("Next question.")]
    [InlineData("the next question")]
    [InlineData("go to the next question")]
    [InlineData("Raven, next question!")]
    [InlineData("show me the next question")]
    [InlineData("nächste Frage")]
    [InlineData("Die nächste Frage.")]
    [InlineData("zur nächsten Frage")]
    [InlineData("weiter, nächste Frage")]
    public void Next_question_said_alone_is_the_command(string said) => SpokenNextQuestion.Is(said).ShouldBeTrue();

    [Theory]
    [InlineData("what is the next question about")]
    [InlineData("next")]
    [InlineData("question")]
    [InlineData("next chat")]
    [InlineData("answer the next question with yes")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_more_or_less_goes_to_the_brain(string? said) => SpokenNextQuestion.Is(said).ShouldBeFalse();
}
