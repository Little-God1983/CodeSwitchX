using CodeSwitchX.Voice.Speech;
using LittleGod.Speech;
using LittleGod.Speech.Kokoro;
using LittleGod.Speech.QwenTts;
using LittleGod.Speech.Sidecar;
using NSubstitute;

namespace CodeSwitchX.Voice.Tests.Speech;

/// <summary>The app's picks given to the package's engines (#328): the one mapping from the app's enums.</summary>
public sealed class SpeechSettingsTests
{
    private readonly ISpeechEngine _kokoro = Engine(KokoroEngine.Definition);
    private readonly ISpeechEngine _qwen = Engine(QwenTtsEngine.Definition);
    private readonly SpeechEngines _engines;
    private readonly SpeechSettings _settings;

    public SpeechSettingsTests()
    {
        _engines = new SpeechEngines([_kokoro, _qwen]);
        _settings = new SpeechSettings(_engines);
    }

    private static ISpeechEngine Engine(SidecarEngine definition)
    {
        var engine = Substitute.For<ISpeechEngine>();
        engine.Engine.Returns(definition);
        engine.Status.Returns(TextToSpeechStatus.Off);
        return engine;
    }

    [Fact]
    public void At_the_start_no_engine_is_picked_and_each_engine_has_the_default_voice_and_the_small_model()
    {
        _engines.Picked.ShouldBeNull();
        _kokoro.Voice.ShouldBe("af_heart");
        _qwen.Voice.ShouldBe("ryan");
        _qwen.Model.ShouldBe(QwenTtsModels.Small);
    }

    [Theory]
    [InlineData(SpeechEngine.Kokoro, "kokoro")]
    [InlineData(SpeechEngine.Qwen, "qwen")]
    public void Picking_an_engine_picks_it_in_the_switch(SpeechEngine engine, string key)
    {
        _settings.Engine = engine;

        _engines.Picked.ShouldBe(key);
    }

    [Fact]
    public void Picking_none_leaves_the_switch_without_an_engine_and_says_so()
    {
        _settings.Engine = SpeechEngine.Qwen;
        var changes = 0;
        _settings.EngineChanged += (_, _) => changes++;

        _settings.Engine = null;

        _engines.Picked.ShouldBeNull();
        changes.ShouldBe(1);
    }

    [Fact]
    public void A_voice_picked_is_set_on_its_engine()
    {
        _settings.KokoroVoice = "bm_george";
        _settings.QwenVoice = "sohee";

        _kokoro.Voice.ShouldBe("bm_george");
        _qwen.Voice.ShouldBe("sohee");
    }

    [Fact]
    public void A_voice_the_engine_does_not_have_falls_back_to_its_default_before_it_gets_there()
    {
        _settings.QwenVoice = "sohee";

        _settings.QwenVoice = "bm_george"; // Kokoro's, not Qwen's

        _settings.QwenVoice.ShouldBe("ryan");
        _qwen.Voice.ShouldBe("ryan");
    }

    [Fact]
    public void The_large_model_is_set_on_Qwen()
    {
        _settings.Model = SpeechModel.Large;

        _qwen.Model.ShouldBe(QwenTtsModels.Large);
    }

    [Fact]
    public void The_voices_are_the_engines_own_in_their_order()
    {
        SpeechSettings.VoicesOf(SpeechEngine.Kokoro).ShouldBe(KokoroEngine.Voices.All);
        SpeechSettings.VoicesOf(SpeechEngine.Qwen)[0].Id.ShouldBe("ryan");
    }
}
