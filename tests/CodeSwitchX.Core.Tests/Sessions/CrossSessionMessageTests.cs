using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Core.Tests.Sessions;

public class CrossSessionMessageTests
{
    // As a chat in VS Code received Raven's task on 2026-10-06 (CLI 2.1.291).
    private const string FromRaven = """
        Another Claude session sent a message:
        <cross-session-message from="uds:\\.\pipe\LOCAL\cc-msg-4fbd2cd19fc698d8b7ce1d73eef30688" from-name="raven-72" from-mode="prompting">
        Create a bug report for F keys not working
        </cross-session-message>
        """;

    [Fact]
    public void A_message_names_the_messaging_socket_of_the_session_that_sent_it()
    {
        CrossSessionMessage.SenderOf(FromRaven).ShouldBe(@"\\.\pipe\LOCAL\cc-msg-4fbd2cd19fc698d8b7ce1d73eef30688");
    }

    [Fact]
    public void A_message_without_the_line_before_it_names_its_sender_too()
    {
        CrossSessionMessage.SenderOf("""<cross-session-message from="uds:\\.\pipe\LOCAL\cc-msg-1" from-name="raven-72">Hi</cross-session-message>""")
            .ShouldBe(@"\\.\pipe\LOCAL\cc-msg-1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Fix the build")]
    [InlineData("""Why does <cross-session-message from="uds:x"> show up in the title?""")]
    [InlineData("""<cross-session-message from-name="raven-72">Hi</cross-session-message>""")]
    [InlineData("""<cross-session-message from="">Hi</cross-session-message>""")]
    [InlineData("""<cross-session-message from="uds:\\.\pipe\LOCAL\cc-msg-1""")]
    public void A_prompt_that_is_no_message_or_names_no_sender_has_none(string? prompt)
    {
        CrossSessionMessage.SenderOf(prompt).ShouldBeNull();
    }

    [Fact]
    public void A_sender_named_only_in_the_message_s_text_is_none()
    {
        CrossSessionMessage.SenderOf("""<cross-session-message from-name="x">see from="uds:\\.\pipe\LOCAL\cc-msg-1"</cross-session-message>""")
            .ShouldBeNull("only the opening tag says who sent it");
    }
}
