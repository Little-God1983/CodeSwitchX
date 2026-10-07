using CodeSwitchX.Core;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Ingest.Hooks;

namespace CodeSwitchX.Ingest.Tests.Hooks;

public class RavenMessagesTests
{
    private const string Socket = @"\\.\pipe\LOCAL\cc-msg-4fbd2cd19fc698d8b7ce1d73eef30688";
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "csx-raven-messages"));
    private readonly List<(string Socket, string Folder)> _asked = [];
    private readonly RavenMessages _messages;

    public RavenMessagesTests() => _messages = new RavenMessages(_paths, (socket, folder) =>
    {
        _asked.Add((socket, folder));
        return socket == Socket;
    });

    private static HookEvent Event(string name, string? prompt, string? agent = null) => new()
    {
        SessionId = "s1",
        EventName = name,
        At = DateTimeOffset.UnixEpoch,
        Prompt = prompt,
        AgentId = agent,
    };

    private static string From(string socket) =>
        $"<cross-session-message from=\"uds:{socket}\" from-name=\"raven-72\">Create a bug report</cross-session-message>";

    [Fact]
    public void A_message_from_a_session_running_in_raven_s_folder_tells_the_chat_who_sent_it()
    {
        _messages.ContextFor(Event("UserPromptSubmit", From(Socket))).ShouldBe(RavenMessages.Context);

        _asked.ShouldBe([(Socket, _paths.RavenDirectory)]);
    }

    [Fact]
    public void Only_the_chat_s_own_prompt_is_told_and_only_what_comes_from_raven()
    {
        _messages.ContextFor(Event("UserPromptSubmit", From(Socket), agent: "a1")).ShouldBeNull("a sub-agent asks no one");
        _messages.ContextFor(Event("SessionStart", From(Socket))).ShouldBeNull();
        _messages.ContextFor(Event("UserPromptSubmit", From(@"\\.\pipe\LOCAL\cc-msg-other"))).ShouldBeNull();
        _messages.ContextFor(Event("UserPromptSubmit", "Fix the build")).ShouldBeNull();
    }

    [Fact]
    public void A_prompt_that_is_no_message_is_not_looked_up()
    {
        _messages.ContextFor(Event("UserPromptSubmit", "Fix the build"));

        _asked.ShouldBeEmpty("the sessions folder is read only for a message from another session");
    }
}
