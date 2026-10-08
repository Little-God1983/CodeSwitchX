using CodeSwitchX.Core.Yard;

namespace CodeSwitchX.Core.Tests.Yard;

public class WhisperNoiseTests
{
    [Theory]
    [InlineData("Thank you.")]
    [InlineData("Thanks for watching!")]
    [InlineData(" you")]
    [InlineData("Untertitel im Auftrag des ZDF, 2021")]
    [InlineData("Vielen Dank.")]
    public void Whispers_phantom_phrases_are_noise(string text) => WhisperNoise.Is(text).ShouldBeTrue();

    [Theory]
    [InlineData("Thank you, and open chat five.")]
    [InlineData("And then tell me about chat five.")]
    [InlineData("Yes.")]
    [InlineData("")]
    [InlineData(null)]
    public void Words_with_a_meaning_are_not(string? text) => WhisperNoise.Is(text).ShouldBeFalse();
}
