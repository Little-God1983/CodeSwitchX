using CodeSwitchX.Core.Yard;

namespace CodeSwitchX.Core.Tests.Yard;

/// <summary>#250: "stop" said alone silences Raven, and goes to no brain.</summary>
public class SpokenStopTests
{
    [Theory]
    [InlineData("stop")]
    [InlineData("Stop.")]
    [InlineData("Raven, stop.")]
    [InlineData("Raven, stop, please.")]
    [InlineData("Okay, enough!")]
    [InlineData("That's enough.")]
    [InlineData("Be quiet.")]
    [InlineData("shut up")]
    [InlineData("Stop talking.")]
    [InlineData("Hör auf!")]
    [InlineData("Ruhe bitte.")]
    [InlineData("Das reicht jetzt.")]
    public void A_stop_said_alone_is_the_command(string said) => SpokenStop.Is(said).ShouldBeTrue();

    [Theory]
    [InlineData("stop chat 2")]
    [InlineData("stop it")] // the window's chat, for its brain
    [InlineData("stop the build")]
    [InlineData("Raven, stop the upload chat.")]
    [InlineData("why did it stop")]
    [InlineData("is that enough")]
    [InlineData("quiet hours start at ten")]
    [InlineData("Raven")]
    [InlineData("please")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_more_or_less_goes_to_the_brain(string? said) => SpokenStop.Is(said).ShouldBeFalse();
}
