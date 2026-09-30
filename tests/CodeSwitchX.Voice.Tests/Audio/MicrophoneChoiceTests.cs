namespace CodeSwitchX.Voice.Tests.Audio;

using CodeSwitchX.Voice.Audio;

public sealed class MicrophoneChoiceTests
{
    private static readonly MicrophoneDevice Rode = new("{id-rode}", "Microphone (RØDE Connect Virtual)");
    private static readonly MicrophoneDevice Headset = new("{id-headset}", "Headset (Arctis Nova 7)");

    [Fact]
    public void The_stored_device_is_used_when_it_is_still_there()
    {
        MicrophoneChoice.Resolve([Rode, Headset], Headset, Rode)
            .ShouldBe(new MicrophoneChoiceResult(Headset, MicrophoneChoiceOutcome.Stored));
    }

    [Fact]
    public void A_stored_device_with_a_new_id_is_found_again_by_name()
    {
        var moved = Headset with { Id = "{id-headset-2}" };
        MicrophoneChoice.Resolve([Rode, moved], Headset, Rode)
            .ShouldBe(new MicrophoneChoiceResult(moved, MicrophoneChoiceOutcome.Relocated));
    }

    [Fact]
    public void Two_devices_with_the_stored_name_count_as_not_found()
    {
        var a = Headset with { Id = "{a}" };
        var b = Headset with { Id = "{b}" };
        MicrophoneChoice.Resolve([Rode, a, b], Headset, Rode)
            .ShouldBe(new MicrophoneChoiceResult(Rode, MicrophoneChoiceOutcome.FellBackToDefault));
    }

    [Fact]
    public void A_missing_device_falls_back_to_the_default()
    {
        MicrophoneChoice.Resolve([Rode], Headset, Rode)
            .ShouldBe(new MicrophoneChoiceResult(Rode, MicrophoneChoiceOutcome.FellBackToDefault));
    }

    [Fact]
    public void A_missing_device_without_a_default_falls_back_to_the_first_device()
    {
        MicrophoneChoice.Resolve([Rode], Headset, null)
            .ShouldBe(new MicrophoneChoiceResult(Rode, MicrophoneChoiceOutcome.FellBackToDefault));
    }

    [Fact]
    public void Without_a_stored_choice_the_default_is_used()
    {
        MicrophoneChoice.Resolve([Rode, Headset], null, Rode)
            .ShouldBe(new MicrophoneChoiceResult(Rode, MicrophoneChoiceOutcome.Stored));
    }

    [Fact]
    public void Without_a_default_the_first_device_is_used()
    {
        MicrophoneChoice.Resolve([Headset], null, null)
            .ShouldBe(new MicrophoneChoiceResult(Headset, MicrophoneChoiceOutcome.Stored));
    }

    [Fact]
    public void No_devices_means_no_microphone()
    {
        MicrophoneChoice.Resolve([], Headset, null)
            .ShouldBe(new MicrophoneChoiceResult(null, MicrophoneChoiceOutcome.NoDevices));
    }
}
