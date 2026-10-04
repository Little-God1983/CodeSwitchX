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

    /// <summary>
    /// A chat Raven starts or messages gets its task in SendMessage's envelope, as the hook's prompt and as the
    /// transcript's first prompt. The envelope's address is no title: the task inside it is.
    /// </summary>
    [Fact]
    public void A_cross_session_message_is_titled_by_the_task_inside_it()
    {
        const string prompt = """
            <cross-session-message from="uds:\\.\pipe\LOCAL\cc-msg-6bd285a7f461b286b0e70e3149f43d79" from-name="raven-22" from-mode="prompting">
            Create a new GitHub issue to extend the MCP so that CodeSwitchX can minimize and maximize the CodeSwitchX window.
            </cross-session-message>
            """;

        ChatTitle.FromPrompt(prompt, 60).ShouldBe("Create a new GitHub issue to extend the MCP so that CodeSwi…");
    }

    [Fact]
    public void Text_around_a_cross_session_message_is_left_out()
    {
        const string prompt = """
            Another Claude session sent a message: <cross-session-message from="uds:\\.\pipe\LOCAL\cc-msg-1" from-name="raven-3">
            Fix the build
            </cross-session-message>  This came from another Claude session, not typed by your user.
            """;

        ChatTitle.FromPrompt(prompt).ShouldBe("Fix the build");
    }

    [Fact]
    public void A_cross_session_message_cut_off_before_its_end_keeps_the_task_it_has()
    {
        ChatTitle.FromPrompt("""<cross-session-message from="uds:x" from-name="raven-3">Fix the bu""").ShouldBe("Fix the bu");
    }

    /// <summary>
    /// Titles stored before the fix are the envelope's start, cut off inside its opening tag: no task is left in them,
    /// so they are no title (the next prompt names the chat).
    /// </summary>
    [Theory]
    [InlineData("""<cross-session-message from="uds:\\.\pipe\LOCAL\cc-msg-6dc7…""")]
    [InlineData("""<cross-session-message from="uds:x" from-name="raven-3"></cross-session-message>""")]
    public void A_cross_session_message_without_a_task_gives_no_title(string prompt)
    {
        ChatTitle.FromPrompt(prompt).ShouldBeNull();
    }

    [Fact]
    public void A_prompt_that_only_mentions_the_tag_is_kept()
    {
        ChatTitle.FromPrompt("Why does the title show <cross-session-message>?").ShouldBe("Why does the title show <cross-session-message>?");
    }

    /// <summary>Only a prompt that is a cross-session message is one: a prompt that quotes the tag (a summary, a bug report) is the user's.</summary>
    [Fact]
    public void A_prompt_that_quotes_a_whole_tag_is_kept()
    {
        ChatTitle.FromPrompt("""Why is the title <cross-session-message from="uds:x"> shown?""", int.MaxValue)
            .ShouldBe("""Why is the title <cross-session-message from="uds:x"> shown?""");
    }

    [Fact]
    public void A_limit_of_one_gives_just_the_ellipsis()
    {
        ChatTitle.FromPrompt("ab", 1).ShouldBe("…");
    }
}
