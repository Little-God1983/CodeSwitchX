using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Tests.Raven;
using CodeSwitchX.UI.Yard;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>#386: what a chat row's right-click does: Compact now, and Keep warm.</summary>
public sealed class ChatRowMenuTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Start);
    private readonly FakeYardDirectory _yard = new();
    private readonly IYardActions _actions = Substitute.For<IYardActions>();
    private readonly Dialogs _dialogs = new();
    private readonly Dictionary<string, SessionSnapshot> _sessions = new(StringComparer.Ordinal);
    private readonly KeepWarm _keepWarm;
    private readonly ChatRowMenu _menu;

    public ChatRowMenuTests()
    {
        _keepWarm = new KeepWarm(id => _sessions.GetValueOrDefault(id), id => _sessions.ContainsKey(id) ? "ws-" + id : null, Substitute.For<IKeepAliveSender>(),
            Substitute.For<ISettingsStore>(), _time, NullLogger<KeepWarm>.Instance);
        _menu = new ChatRowMenu(() => _yard, () => _actions, _keepWarm, _dialogs, NullLogger<ChatRowMenu>.Instance);
        _yard.Show("a", "CodeSwitchX", "Fix the upload");
        _actions.CompactChatNowAsync(Arg.Any<YardChat>(), Arg.Any<CancellationToken>()).Returns("compacted");
    }

    /// <summary>A chat in a tab that made a request ten minutes ago.</summary>
    private void Warm(string id) => _sessions[id] = new SessionSnapshot
    {
        SessionId = id, State = SessionState.Idle, StartedAt = Start, LastEventAt = Start, StateSince = Start, LastRequestAt = Start.AddMinutes(-10),
    };

    [Fact]
    public async Task An_idle_chat_is_compacted_without_a_question()
    {
        await _menu.CompactAsync("a");

        await _actions.Received(1).CompactChatNowAsync(Arg.Is<YardChat>(c => c.Id == "a" && c.Title == "Fix the upload"), Arg.Any<CancellationToken>());
        _dialogs.Asked.ShouldBeEmpty();
        _dialogs.Told.ShouldBeEmpty("the row shows that it is done");
    }

    [Theory]
    [InlineData(SessionState.Working, false, "The Fix the upload chat is still working. Compacting it now cuts that turn off.\n\nCompact it anyway?")]
    [InlineData(SessionState.Waiting, true, "The Fix the upload chat is waiting for you in the middle of its turn. Compacting it now cuts that turn off.\n\nCompact it anyway?")]
    public async Task A_chat_in_a_turn_is_compacted_only_after_a_yes(SessionState state, bool needsYou, string question)
    {
        _yard.Now("a", state, needsYou);
        _dialogs.Answer = false;

        await _menu.CompactAsync("a");

        _dialogs.Asked.ShouldBe([question]);
        await _actions.DidNotReceive().CompactChatNowAsync(Arg.Any<YardChat>(), Arg.Any<CancellationToken>());

        _dialogs.Answer = true;
        await _menu.CompactAsync("a");

        await _actions.Received(1).CompactChatNowAsync(Arg.Is<YardChat>(c => c.Id == "a"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_compaction_that_is_refused_says_why_in_a_box()
    {
        _actions.CompactChatNowAsync(Arg.Any<YardChat>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new YardActionException("Claude Code did not compact the chat: it no longer has the chat's conversation."));

        await _menu.CompactAsync("a");

        _dialogs.Told.ShouldBe(["Claude Code did not compact the chat: it no longer has the chat's conversation."]);
    }

    [Fact]
    public async Task A_compaction_that_breaks_says_so_in_a_box()
    {
        _actions.CompactChatNowAsync(Arg.Any<YardChat>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));

        await _menu.CompactAsync("a");

        _dialogs.Told.ShouldBe(["Compacting the Fix the upload chat failed. The log says why."]);
    }

    [Fact]
    public async Task A_chat_that_left_the_Yard_meanwhile_is_not_compacted()
    {
        await _menu.CompactAsync("gone");

        _dialogs.Told.ShouldBe(["The chat is no longer on the Yard, so it was not compacted."]);
        await _actions.DidNotReceive().CompactChatNowAsync(Arg.Any<YardChat>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Whether_a_chat_is_being_compacted_is_what_the_compactor_says()
    {
        _actions.IsCompacting("a").Returns(true);

        _menu.IsCompacting("a").ShouldBeTrue();
        _menu.IsCompacting("b").ShouldBeFalse();
    }

    [Fact]
    public void A_chat_is_kept_warm_for_the_time_chosen_and_no_more_once_stopped()
    {
        Warm("a");
        _menu.WhyNotKeptWarm("a").ShouldBeNull();

        _menu.KeepWarm("a", TimeSpan.FromHours(4));
        _menu.KeptWarmUntil("a").ShouldBe(Start.AddHours(4));

        _menu.StopKeepingWarm("a");
        _menu.KeptWarmUntil("a").ShouldBeNull();
    }

    [Fact]
    public void A_chat_that_cannot_be_kept_warm_says_why()
    {
        _menu.WhyNotKeptWarm("a").ShouldBe("Only a chat open in a VS Code tab can be kept warm.");

        _menu.KeepWarm("a", TimeSpan.FromHours(4));

        _menu.KeptWarmUntil("a").ShouldBeNull();
        _dialogs.Told.ShouldBe(["Only a chat open in a VS Code tab can be kept warm."], "it changed between the menu opening and the click");
    }

    private sealed class Dialogs : IYardDialogs
    {
        public bool Answer { get; set; } = true;
        public List<string> Asked { get; } = [];
        public List<string> Told { get; } = [];

        public bool Confirm(string question)
        {
            Asked.Add(question);
            return Answer;
        }

        public void Tell(string message) => Told.Add(message);
    }
}
