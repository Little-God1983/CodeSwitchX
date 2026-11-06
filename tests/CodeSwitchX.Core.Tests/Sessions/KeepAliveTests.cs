using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.Core.Tests.Sessions;

public class KeepAliveTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero));
    private readonly SessionEngine _engine;

    public KeepAliveTests()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        _engine = new SessionEngine(bus, new WorkspaceResolver(), _time, NullLogger<SessionEngine>.Instance);
        _engine.Start();
    }

    /// <summary>The ping as it reaches the chat: SendMessage's envelope around the text.</summary>
    private static string Ping(string text = KeepAlive.Text) =>
        $"""
        Another Claude session sent a message:
        <cross-session-message from="uds:\\.\pipe\LOCAL\cc-msg-1" from-name="keepwarm-3f" from-mode="prompting">
        {text}
        </cross-session-message>
        """;

    private HookEvent Prompt(string prompt) => new()
    {
        SessionId = "s1",
        EventName = "UserPromptSubmit",
        Signal = SessionSignal.PromptSubmit,
        At = _time.GetUtcNow(),
        Cwd = @"C:\Repo\App",
        Prompt = prompt,
    };

    private HookEvent Stop() => new() { SessionId = "s1", EventName = "Stop", Signal = SessionSignal.Stop, At = _time.GetUtcNow() };

    [Fact]
    public void A_message_from_another_session_with_the_keep_alive_text_is_a_ping()
    {
        KeepAlive.IsPing(Ping()).ShouldBeTrue();
    }

    [Fact]
    public void A_ping_is_told_by_its_opening_words_however_the_sender_worded_the_rest()
    {
        KeepAlive.IsPing(Ping("Keep-alive from CodeSwitchX. Please just say ok.")).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Fix the upload retry")]
    [InlineData(KeepAlive.Text)] // typed by the user: no message of another session
    public void A_prompt_of_the_users_own_is_no_ping(string? prompt)
    {
        KeepAlive.IsPing(prompt).ShouldBeFalse();
    }

    [Fact]
    public void Another_sessions_message_about_something_else_is_no_ping()
    {
        KeepAlive.IsPing(Ping("Run the tests and tell the user what failed.")).ShouldBeFalse();
    }

    [Fact]
    public void A_ping_starts_a_keep_alive_turn()
    {
        _engine.Apply(Prompt(Ping()));

        var chat = _engine.Get("s1")!;
        chat.State.ShouldBe(SessionState.Working);
        chat.KeepAliveTurn.ShouldBeTrue();
    }

    [Fact]
    public void The_turn_is_still_a_keep_alive_turn_when_it_has_ended()
    {
        _engine.Apply(Prompt(Ping()));
        _time.Advance(TimeSpan.FromSeconds(5));
        _engine.Apply(Stop());

        var chat = _engine.Get("s1")!;
        chat.State.ShouldBe(SessionState.Idle);
        chat.KeepAliveTurn.ShouldBeTrue("its end is told apart from a turn of the user's by it");
    }

    [Fact]
    public void The_users_next_prompt_starts_a_turn_of_their_own()
    {
        _engine.Apply(Prompt(Ping()));
        _engine.Apply(Stop());
        _time.Advance(TimeSpan.FromMinutes(1));

        _engine.Apply(Prompt("Now fix the upload retry"));

        _engine.Get("s1")!.KeepAliveTurn.ShouldBeFalse();
    }

    [Fact]
    public void A_ping_is_no_use_of_the_chat()
    {
        _engine.Apply(Prompt("Fix the upload retry"));
        _engine.Apply(Stop());
        var used = _engine.Get("s1")!.LastUseAt;
        _time.Advance(TimeSpan.FromMinutes(55));

        _engine.Apply(Prompt(Ping()));

        _engine.Get("s1")!.LastUseAt.ShouldBe(used, "the user did nothing in it: its tile and row stay where they were");
    }
}
