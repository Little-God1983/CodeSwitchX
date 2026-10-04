using CodeSwitchX.Core.Yard;

namespace CodeSwitchX.Core.Tests.Yard;

public sealed class SpokenNumberTests
{
    [Theory]
    [InlineData("3", 3)]
    [InlineData("three", 3)]
    [InlineData("Three.", 3)]
    [InlineData("drei", 3)]
    [InlineData("number three", 3)]
    [InlineData("Nummer drei", 3)]
    [InlineData("chat 3", 3)]
    [InlineData("Chat drei", 3)]
    [InlineData("chat number twelve", 12)]
    [InlineData("workspace eins", 1)]
    [InlineData("chat zero", 0)]
    [InlineData("Chat null", 0)]
    [InlineData("twenty", 20)]
    [InlineData("zwanzig", 20)]
    [InlineData("42", 42)]
    public void A_number_said_alone_or_after_chat_workspace_or_number_is_read(string said, int number)
    {
        SpokenNumber.TryRead(said, out var read).ShouldBeTrue();
        read.ShouldBe(number);
    }

    [Theory]
    [InlineData("")]
    [InlineData("chat")]
    [InlineData("CodeSwitchX")]
    [InlineData("three chats")]
    [InlineData("chat three please stop")]
    [InlineData("Raven 2")]
    [InlineData("-1")]
    public void Anything_else_is_no_number(string said)
    {
        SpokenNumber.TryRead(said, out _).ShouldBeFalse();
    }
}
