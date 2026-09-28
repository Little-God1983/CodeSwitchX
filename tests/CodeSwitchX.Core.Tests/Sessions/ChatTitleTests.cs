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

    [Theory]
    [InlineData(58, "😀")] // one surrogate pair across the cut
    [InlineData(56, "🇩🇪")] // a flag: two pairs, the cut between them
    [InlineData(52, "👨‍👩‍👧")] // a family: three emoji joined with ZWJ
    public void A_cut_never_leaves_part_of_an_emoji_before_the_ellipsis(int letters, string emoji)
    {
        // The emoji reaches past the cut at 59 chars; keeping what fits would keep its first part alone.
        var title = ChatTitle.FromPrompt(new string('a', letters) + emoji + " and more", 60)!;

        title.ShouldBe(new string('a', letters) + "…");
    }

    [Fact]
    public void A_limit_of_one_gives_just_the_ellipsis()
    {
        ChatTitle.FromPrompt("ab", 1).ShouldBe("…");
    }
}
