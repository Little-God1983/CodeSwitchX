using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Core.Tests.Sessions;

public class ChatTitleTests
{
    [Fact]
    public void Short_prompts_are_kept_as_one_line()
    {
        ChatTitle.FromPrompt("Fix the\r\n  build").ShouldBe("Fix the build");
    }

    [Fact]
    public void Long_prompts_are_cut_to_the_limit_with_an_ellipsis()
    {
        var title = ChatTitle.FromPrompt(new string('a', 100), 60)!;

        title.Length.ShouldBe(60);
        title.ShouldEndWith("…");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_prompts_give_no_title(string? prompt)
    {
        ChatTitle.FromPrompt(prompt).ShouldBeNull();
    }
}
