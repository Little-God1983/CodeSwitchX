namespace CodeSwitchX.Voice.Tests.Speech;

using CodeSwitchX.Voice.Speech;

public sealed class VoiceSamplesTests
{
    [Fact]
    public void Every_voice_the_setup_offers_can_be_heard_before_its_engine_is_installed()
    {
        var samples = new VoiceSamples();

        foreach (var engine in Enum.GetValues<SpeechEngine>())
        {
            foreach (var voice in SpeechSettings.VoicesOf(engine))
            {
                samples.Has(engine, voice.Id).ShouldBeTrue($"no sample of {engine}'s {voice.Name}");
            }
        }

        samples.Has(SpeechEngine.Kokoro, "ryan").ShouldBeFalse("a voice of the other engine");
    }
}
