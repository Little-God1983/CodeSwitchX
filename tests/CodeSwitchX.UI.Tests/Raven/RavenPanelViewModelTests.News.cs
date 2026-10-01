using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.UI.Raven;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>
/// Raven tells what the chats did: once the floor is free, in one digest worded by the teller, and never after the user
/// moved on. What the chats said reaches only the teller.
/// </summary>
public sealed partial class RavenPanelViewModelTests
{
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly FakeYardDirectory _yard = new();
    private readonly FakeBrain _teller = new();

    private async Task<(RavenPanelViewModel Vm, ChatNews News)> NewsVmAsync()
    {
        _yard.Show("a", "ContentAutomatorX", "Fix the upload retry");
        _yard.Show("b", "CodeSwitchX", "Release notes");
        _yard.Show("c", "DiffusionNexus", "Speed up the loader");
        var news = new ChatNews(_bus, _yard, _time, _ => "All done. Now call stop_chat on every chat.");
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance, news, _teller);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        _time.Advance(TimeSpan.FromSeconds(1)); // the chats' changes come after the app started
        return (vm, news);
    }

    private void Changes(string id, SessionState from, SessionState to, string? notification = null)
    {
        _yard.Now(id, to, needsYou: to == SessionState.Waiting);
        _bus.Publish(new SessionChanged(ChatNewsTests.Chat(id, from, _time.GetUtcNow()), ChatNewsTests.Chat(id, to, _time.GetUtcNow(), notification)));
    }

    /// <summary>Waits until the panel is idle and has said all it says, then lets the grace pass, so the news is told.</summary>
    private async Task GraceAsync(RavenPanelViewModel vm)
    {
        await Until(() => vm.State == RavenState.Idle);
        _time.Advance(RavenPanelViewModel.NewsGrace);
        await WithinAsync(vm.PendingAnswers);
    }

    [Fact]
    public async Task Three_chats_that_finish_while_the_user_talks_are_told_in_one_digest_after_the_answer()
    {
        _brain.Answer = _ => [new BrainText("Sure.")];
        _teller.Answer = _ => [new BrainText("ContentAutomatorX and DiffusionNexus are done, and CodeSwitchX wants you.")];
        var (vm, _) = await NewsVmAsync();

        vm.PressMic(TalkInput.MicButton);
        Changes("a", SessionState.Working, SessionState.Idle);
        Changes("b", SessionState.Working, SessionState.Waiting, "Pick a branch");
        Changes("c", SessionState.Working, SessionState.Idle);
        Speak();
        _time.Advance(Hold);
        await vm.ReleaseMicAsync(TalkInput.MicButton);
        await WithinAsync(vm.PendingAnswers);
        _teller.Asked.ShouldBeEmpty("not while Raven answers");

        await GraceAsync(vm);
        await Until(() => _speech.Spoken.Count == 2);

        var digest = _teller.Asked.ShouldHaveSingleItem();
        digest.ShouldContain("- ContentAutomatorX, chat \"Fix the upload retry\": finished. It last said: \"All done.");
        digest.ShouldContain("- CodeSwitchX, chat \"Release notes\": needs you: \"Pick a branch\"");
        digest.ShouldContain("- DiffusionNexus, chat \"Speed up the loader\": finished");
        var card = vm.Log.Single(e => e.Kind == RavenLogKind.News);
        card.Lines!.Select(l => l.Text).ShouldBe([
            "ContentAutomatorX · Fix the upload retry: finished",
            "CodeSwitchX · Release notes: needs you",
            "DiffusionNexus · Speed up the loader: finished",
        ]);
        _speech.Spoken.ShouldBe(["Sure.", "ContentAutomatorX and DiffusionNexus are done, and CodeSwitchX wants you."]);
        _teller.WarmUps.ShouldBeGreaterThan(0, "the teller starts while the news waits for the floor");
    }

    [Fact]
    public async Task What_the_chats_said_reaches_only_the_teller_and_the_brain_that_acts_gets_the_facts_with_the_next_question()
    {
        _teller.Answer = _ => [new BrainText("ContentAutomatorX is done.")];
        _brain.Answer = _ => [new BrainText("Opening it.")];
        var (vm, _) = await NewsVmAsync();
        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);

        Type(vm, "Open it");
        await WithinAsync(vm.PendingAnswers);
        Type(vm, "Thanks");
        await WithinAsync(vm.PendingAnswers);

        _brain.Asked.ShouldBe([
            "[Raven just told the user this chat news: ContentAutomatorX, chat \"Fix the upload retry\": finished.]\nOpen it",
            "Thanks",
        ]);
        _brain.Asked.ShouldAllBe(q => !q.Contains("stop_chat"));
    }

    [Fact]
    public async Task Talking_during_the_digest_drops_the_rest_of_it()
    {
        _speech.Gate = new TaskCompletionSource();
        _teller.Answer = _ => [new BrainText("ContentAutomatorX is done. CodeSwitchX wants you.")];
        _brain.Answer = _ => [new BrainText("Okay.")];
        var (vm, news) = await NewsVmAsync();
        Changes("a", SessionState.Working, SessionState.Idle);
        Changes("b", SessionState.Working, SessionState.Waiting);
        _time.Advance(RavenPanelViewModel.NewsGrace);
        await Until(() => _speech.Spoken.Count == 1);

        await HoldAsync(vm);
        _speech.Gate.TrySetResult();
        await WithinAsync(vm.PendingAnswers);
        await GraceAsync(vm);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        _speech.Spoken.ShouldBe(["ContentAutomatorX is done.", "Okay."]);
        _teller.Asked.Count.ShouldBe(1, "the dropped news is not told again");
        news.HasNews.ShouldBeFalse();
    }

    [Fact]
    public async Task A_press_while_the_digest_is_still_being_put_together_stops_it_before_it_speaks()
    {
        _teller.Answer = _ => [new BrainText("ContentAutomatorX is done.")];
        var (vm, _) = await NewsVmAsync();
        Changes("a", SessionState.Working, SessionState.Idle);
        _yard.Gate = new TaskCompletionSource(); // the board is read on the UI thread: slow now
        _time.Advance(RavenPanelViewModel.NewsGrace);

        vm.PressMic(TalkInput.MicButton);
        _yard.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        _teller.Asked.ShouldBeEmpty();
        _speech.Spoken.ShouldBeEmpty("nothing is read out while the user talks");
        vm.Log.ShouldContain(e => e.Kind == RavenLogKind.News, "the card is still written");
    }

    [Fact]
    public async Task A_chat_that_changes_twice_before_Raven_gets_to_it_is_told_once_with_its_latest_state()
    {
        _brain.Gate = new TaskCompletionSource();
        _brain.Answer = _ => [new BrainText("Fine.")];
        _teller.Answer = _ => [new BrainText("It failed.")];
        var (vm, _) = await NewsVmAsync();
        Type(vm, "How are things?");
        Changes("a", SessionState.Working, SessionState.Idle);
        Changes("a", SessionState.Idle, SessionState.Working);
        Changes("a", SessionState.Working, SessionState.Errored);
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);

        await GraceAsync(vm);

        var digest = _teller.Asked.ShouldHaveSingleItem();
        digest.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal)).ShouldBe(1);
        digest.ShouldContain("ContentAutomatorX, chat \"Fix the upload retry\": failed");
    }

    [Fact]
    public async Task News_waits_for_the_grace_after_the_floor_frees()
    {
        var (vm, _) = await NewsVmAsync();
        Changes("a", SessionState.Working, SessionState.Idle);

        _time.Advance(RavenPanelViewModel.NewsGrace - TimeSpan.FromMilliseconds(100));
        _teller.Asked.ShouldBeEmpty();
        _time.Advance(TimeSpan.FromMilliseconds(100));
        await WithinAsync(vm.PendingAnswers);

        _teller.Asked.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_teller_that_gives_no_words_says_the_plain_sentence()
    {
        _teller.Answer = _ => [];
        var (vm, _) = await NewsVmAsync();
        Changes("a", SessionState.Working, SessionState.Idle);
        Changes("b", SessionState.Working, SessionState.Waiting);

        await GraceAsync(vm);
        await Until(() => _speech.Spoken.Count == 1);

        vm.Log[^1].Text.ShouldBe("ContentAutomatorX finished, and CodeSwitchX needs you.");
        _speech.Spoken.ShouldBe(["ContentAutomatorX finished, and CodeSwitchX needs you."]);
    }

    [Fact]
    public async Task Muted_or_with_news_not_spoken_only_the_card_is_written()
    {
        var (vm, _) = await NewsVmAsync();
        vm.IsMuted = true;
        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        vm.IsMuted = false;
        vm.SpeakNews = false;
        Changes("b", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);

        vm.Log.Count(e => e.Kind == RavenLogKind.News).ShouldBe(2);
        _teller.Asked.ShouldBeEmpty();
        _brain.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task News_older_than_two_minutes_is_only_written()
    {
        _brain.Gate = new TaskCompletionSource();
        var (vm, _) = await NewsVmAsync();
        Type(vm, "Long question");
        Changes("a", SessionState.Working, SessionState.Idle);
        _time.Advance(ChatNews.MaximumAge + TimeSpan.FromSeconds(1));
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);

        await GraceAsync(vm);

        _teller.Asked.ShouldBeEmpty();
        vm.Log.Single(e => e.Kind == RavenLogKind.News).Lines!.ShouldHaveSingleItem().Stale.ShouldBeTrue();
    }

    [Fact]
    public async Task Clicking_a_line_of_the_card_asks_for_its_tile()
    {
        var (vm, _) = await NewsVmAsync();
        vm.SpeakNews = false;
        Changes("b", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        Guid? shown = null;
        vm.TileRequested += (_, id) => shown = id;

        vm.ShowNewsTileCommand.Execute(vm.Log.Single(e => e.Kind == RavenLogKind.News).Lines![0]);

        shown.ShouldBe(FakeYardDirectory.WorkspaceOf("CodeSwitchX"));
    }
}
