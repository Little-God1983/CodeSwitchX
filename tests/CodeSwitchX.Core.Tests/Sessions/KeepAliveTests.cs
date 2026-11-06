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

    private HookEvent JobStarts(string job) => new()
    {
        SessionId = "s1", EventName = "PostToolUse", Signal = SessionSignal.ToolUse, At = _time.GetUtcNow(), BackgroundJob = job,
    };

    private void JobEnds(string job) => _engine.Apply(new TranscriptUpdate
    {
        SessionId = "s1", TranscriptPath = "p", ObservedAt = _time.GetUtcNow(), LastActivityAt = _time.GetUtcNow(),
        JobsEnded = [new BackgroundJobEvent(job, _time.GetUtcNow())],
    });

    [Fact]
    public void A_job_that_ends_after_the_ping_turn_is_news_of_the_users_own_turn()
    {
        _engine.Apply(Prompt("Run the build in the background"));
        _engine.Apply(JobStarts("job1"));
        _engine.Apply(Stop());
        _time.Advance(TimeSpan.FromMinutes(55));
        _engine.Apply(Prompt(Ping()));
        _engine.Apply(Stop());
        var pinged = _engine.Get("s1")!;
        pinged.State.ShouldBe(SessionState.Working, "the job is still open");
        pinged.KeepAliveTurn.ShouldBeTrue("this is the ping's own end");

        _time.Advance(TimeSpan.FromMinutes(5));
        JobEnds("job1");
        _engine.Get("s1")!.KeepAliveTurn.ShouldBeFalse("the ping's turn is over; what comes now is the user's");
        _engine.Apply(Stop()); // the turn that reacts to the job's end

        var done = _engine.Get("s1")!;
        done.State.ShouldBe(SessionState.Idle);
        done.KeepAliveTurn.ShouldBeFalse("the build the user started is done: that is news");
    }

    [Fact]
    public void A_ping_does_not_title_a_chat()
    {
        _engine.Apply(new HookEvent { SessionId = "s1", EventName = "SessionStart", Signal = SessionSignal.SessionStart, At = _time.GetUtcNow(), Cwd = @"C:\Repo\App" });

        _engine.Apply(Prompt(Ping()));
        _engine.Apply(Stop());

        _engine.Get("s1")!.Title.ShouldBeNull("the app's own words are no title for the user's chat");
    }

    [Fact]
    public void A_message_from_another_session_with_the_keep_alive_text_is_a_ping()
    {
        KeepAlive.IsPing(Ping()).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Keep-alive from CodeSwitchX. Please just say ok.")]
    [InlineData("\"Keep-alive from CodeSwitchX: reply with only ok.\"")]
    [InlineData("**Keep-alive from CodeSwitchX** - reply with only ok.")]
    [InlineData("Message from CodeSwitchX: " + KeepAlive.Text)]
    public void A_ping_is_told_by_its_opening_words_however_the_sender_worded_the_rest(string text)
    {
        KeepAlive.IsPing(Ping(text)).ShouldBeTrue("the sender is a model told to copy the message, and may quote it or put a word before it");
    }

    [Fact]
    public void Another_sessions_message_that_only_mentions_the_ping_is_no_ping()
    {
        KeepAlive.IsPing(Ping("Ask the user what the Keep-alive from CodeSwitchX message in your history was about.")).ShouldBeFalse(
            "Raven passes on what the user said, which may name the ping");
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
