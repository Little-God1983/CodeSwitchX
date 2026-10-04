using CodeSwitchX.Core.Yard;

namespace CodeSwitchX.Core.Tests.Yard;

public class SpokenChatSwitchTests
{
    [Theory]
    [InlineData("chat three", 3)]
    [InlineData("Chat 3.", 3)]
    [InlineData("chat number three", 3)]
    [InlineData("go to chat three", 3)]
    [InlineData("switch to chat 3", 3)]
    [InlineData("Chat drei", 3)]
    [InlineData("zu Chat drei", 3)]
    [InlineData("Wechsel zu Chat Nummer elf", 11)]
    [InlineData("chat zero", 0)]
    [InlineData("Chat null", 0)]
    [InlineData("chat twenty", 20)]
    [InlineData("Raven, chat seven!", 7)]
    public void A_chat_named_by_its_number_is_a_switch(string said, int number)
    {
        SpokenChatSwitch.TryRead(said, out var target).ShouldBeTrue();
        target.ShouldBe(new ChatSwitch(number, Activity: false, Open: false));
    }

    [Theory]
    [InlineData("activity")]
    [InlineData("Aktivität")]
    [InlineData("go to activity")]
    [InlineData("zu Aktivität")]
    public void Activity_is_a_switch(string said)
    {
        SpokenChatSwitch.TryRead(said, out var target).ShouldBeTrue();
        target.ShouldBe(new ChatSwitch(null, Activity: true, Open: false));
    }

    [Theory]
    [InlineData("open chat three", 3)]
    [InlineData("Öffne Chat drei", 3)]
    [InlineData("open chat 12", 12)]
    public void Open_switches_and_opens(string said, int number)
    {
        SpokenChatSwitch.TryRead(said, out var target).ShouldBeTrue();
        target.ShouldBe(new ChatSwitch(number, Activity: false, Open: true));
    }

    /// <summary>Only the whole of what was said is a switch: a sentence that mentions a chat goes to the brain.</summary>
    [Theory]
    [InlineData("three")]
    [InlineData("chat")]
    [InlineData("what is chat three doing")]
    [InlineData("chat three is done")]
    [InlineData("start a chat in CodeSwitchX")]
    [InlineData("three chats")]
    [InlineData("open activity")]
    [InlineData("chat three four")]
    [InlineData("go to the audio one")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_no_switch(string? said)
    {
        SpokenChatSwitch.TryRead(said, out _).ShouldBeFalse();
    }
}
