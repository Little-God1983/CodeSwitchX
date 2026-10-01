using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Raven;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.UI.Tests.Raven;

public sealed class ChatNewsTests : IDisposable
{
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeYardDirectory _yard = new();
    private readonly ChatNews _news;

    public ChatNewsTests()
    {
        _news = new ChatNews(_bus, _yard, _time, path => path is null ? null : $"said in {path}");
        _yard.Show("a", "ContentAutomatorX", "Fix the upload retry");
        _yard.Show("b", "CodeSwitchX", "Release notes");
        _yard.Show("c", "DiffusionNexus", "Speed up the loader");
        _time.Advance(TimeSpan.FromSeconds(1));
    }

    public void Dispose() => _news.Dispose();

    internal static SessionSnapshot Chat(string id, SessionState state, DateTimeOffset since, string? notification = null) => new()
    {
        SessionId = id,
        Title = "a chat",
        State = state,
        StartedAt = since,
        LastEventAt = since,
        StateSince = since,
        LastNotification = notification,
        TranscriptPath = $"{id}.jsonl",
    };

    /// <summary>The chat changes, and the board shows it as it now is.</summary>
    private void Change(string id, SessionState from, SessionState to, string? notification = null)
    {
        _yard.Now(id, to, needsYou: to == SessionState.Waiting);
        _bus.Publish(new SessionChanged(Chat(id, from, _time.GetUtcNow()), Chat(id, to, _time.GetUtcNow(), notification)));
    }

    private Task<IReadOnlyList<ChatNewsLine>> TakeAsync() => _news.TakeAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_finished_turn_a_wait_for_the_user_and_a_failure_are_news_named_as_the_Yard_shows_them()
    {
        Change("a", SessionState.Working, SessionState.Idle);
        _time.Advance(TimeSpan.FromSeconds(1));
        Change("b", SessionState.Working, SessionState.Waiting, "Claude needs your permission to use Bash");
        _time.Advance(TimeSpan.FromSeconds(1));
        Change("c", SessionState.Working, SessionState.Errored);

        var lines = await TakeAsync();

        lines.Select(l => (l.Workspace, l.Title, l.Kind, l.Detail, l.LastSaid, l.Stale)).ShouldBe([
            ("ContentAutomatorX", "Fix the upload retry", ChatNewsKind.Finished, (string?)null, (string?)"said in a.jsonl", false),
            ("CodeSwitchX", "Release notes", ChatNewsKind.NeedsYou, "Claude needs your permission to use Bash", null, false),
            ("DiffusionNexus", "Speed up the loader", ChatNewsKind.Failed, null, "said in c.jsonl", false),
        ]);
        lines[0].Text.ShouldBe("ContentAutomatorX · Fix the upload retry: finished");
        (await TakeAsync()).ShouldBeEmpty("taken news is gone");
    }

    [Fact]
    public async Task A_chat_that_changes_twice_before_it_is_told_is_told_once_with_its_latest_news()
    {
        var arrivals = 0;
        _news.Arrived += (_, _) => arrivals++;
        Change("a", SessionState.Working, SessionState.Idle);
        Change("a", SessionState.Idle, SessionState.Working);
        Change("a", SessionState.Working, SessionState.Waiting, "Pick a branch");

        var line = (await TakeAsync()).ShouldHaveSingleItem();

        line.Kind.ShouldBe(ChatNewsKind.NeedsYou);
        line.Detail.ShouldBe("Pick a branch");
        arrivals.ShouldBe(2);
    }

    [Theory]
    [InlineData(SessionState.Idle, SessionState.Working)]
    [InlineData(SessionState.Starting, SessionState.Idle)]
    [InlineData(SessionState.Working, SessionState.Ended)]
    [InlineData(SessionState.Working, SessionState.Stale)]
    [InlineData(SessionState.Waiting, SessionState.Waiting)]
    public async Task Other_changes_are_no_news(SessionState from, SessionState to)
    {
        Change("a", from, to);

        _news.HasNews.ShouldBeFalse();
        (await TakeAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_chat_restored_at_startup_or_one_the_Yard_does_not_show_brings_no_news()
    {
        var before = _time.GetUtcNow() - TimeSpan.FromMinutes(5);
        _bus.Publish(new SessionChanged(null, Chat("a", SessionState.Waiting, _time.GetUtcNow())));
        _bus.Publish(new SessionChanged(Chat("a", SessionState.Working, before), Chat("a", SessionState.Idle, before)));
        var silent = Chat("b", SessionState.Idle, _time.GetUtcNow()) with { Title = null };
        _bus.Publish(new SessionChanged(Chat("b", SessionState.Starting, _time.GetUtcNow()) with { Title = null }, silent));

        (await TakeAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task News_the_chat_has_moved_past_without_new_news_is_not_told()
    {
        Change("a", SessionState.Idle, SessionState.Waiting, "Allow Bash?");
        _yard.Now("a", SessionState.Working); // allowed in VS Code: it works again, which is no news
        Change("b", SessionState.Working, SessionState.Idle);
        _yard.Now("b", SessionState.Working); // a new prompt
        Change("c", SessionState.Working, SessionState.Waiting);
        _yard.Now("c", SessionState.Waiting, needsYou: true);

        (await TakeAsync()).ShouldHaveSingleItem().SessionId.ShouldBe("c");
    }

    [Fact]
    public async Task News_older_than_two_minutes_is_marked_stale_and_a_chat_gone_from_the_board_is_left_out()
    {
        Change("a", SessionState.Working, SessionState.Idle);
        Change("gone", SessionState.Working, SessionState.Idle);
        _time.Advance(ChatNews.MaximumAge + TimeSpan.FromSeconds(1));
        Change("b", SessionState.Working, SessionState.Idle);

        var lines = await TakeAsync();

        lines.Select(l => (l.SessionId, l.Stale)).ShouldBe([("a", true), ("b", false)]);
        lines[0].Text.ShouldEndWith("(older than 2 minutes)");
    }
}

/// <summary>A Yard of chats shown by id; every chat has a workspace of its own.</summary>
internal sealed class FakeYardDirectory : IYardDirectory
{
    private readonly List<YardChat> _chats = [];

    public void Show(string id, string workspace, string title) => _chats.Add(new YardChat(id, title, WorkspaceOf(workspace), workspace,
        SessionState.Idle, false, DateTimeOffset.UnixEpoch, "1m", null, null, 0, null, null));

    /// <summary>The chat as the board shows it now.</summary>
    public void Now(string id, SessionState state, bool needsYou = false)
    {
        var index = _chats.FindIndex(c => c.Id == id);
        if (index >= 0)
        {
            _chats[index] = _chats[index] with { State = state, NeedsYou = needsYou };
        }
    }

    public static Guid WorkspaceOf(string workspace) => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(workspace)));

    public Task<IReadOnlyList<YardWorkspace>> WorkspacesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<YardWorkspace>>([]);

    public Task<IReadOnlyList<YardChat>> ChatsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<YardChat>>([.. _chats]);
}
