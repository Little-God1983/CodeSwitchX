namespace CodeSwitchX.Voice.Tests.Dictation;

using CodeSwitchX.Voice.Dictation;

public sealed class DictationOptionsTests
{
    [Fact]
    public void The_default_options_use_large_v3_turbo_with_automatic_language()
    {
        var options = new DictationOptions();

        options.Model.ShouldBe(WhisperModel.LargeV3Turbo);
        options.Language.ShouldBe("auto");
    }
}
