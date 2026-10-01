using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.UI.Raven;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>Raven tells what the chats did: once the floor is free, in one digest, and never after the user moved on.</summary>
public sealed partial class RavenPanelViewModelTests
{
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly FakeYardDirectory _yard = new();

    private const string Digest = "[Chat news";

    private async Task<(RavenPanelViewModel Vm, ChatNews News)> NewsVmAsync()
    {
        _yard.Show("a", "ContentAutomatorX", "Fix the upload retry");
        _yard.Show("b", "CodeSwitchX", "Release notes");
        _yard.Show("c", "DiffusionNexus", "Speed up the loader");
        var news = new ChatNews(_bus, _yard, _time, _ => "All done.");
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance, news);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        _time.Advance(TimeSpan.FromSeconds(1)); // the chats' changes come after the app started
        return (vm, news);
    }

    private void Changes(string id, SessionState from, SessionState to, string? notification = null) => _bus.Publish(new SessionChanged(
        ChatNewsTests.Chat(id, from, _time.GetUtcNow()), ChatNewsTests.Chat(id, to, _time.GetUtcNow(), notification)));

    /// <summary>Waits until the panel is idle and has said all it says, then lets the grace pass, so the news is told.</summary>
    private async Task GraceAsync(RavenPanelViewModel vm)
    {
        await Until(() => vm.State == RavenState.Idle);
        _time.Advance(RavenPanelViewModel.NewsGrace);
        await WithinAsync(vm.PendingAnswers);
    }

    private List<string> DigestsAsked() => _brain.Asked.Where(q => q.StartsWith(Digest, StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task Three_chats_that_finish_while_the_user_talks_are_told_in_one_digest_after_the_answer()
    {
        _brain.Answer = q => q.StartsWith(Digest, StringComparison.Ordinal)
            ? [new BrainText("ContentAutomatorX and DiffusionNexus are done, and CodeSwitchX wants you.")]
            : [new BrainText("Sure.")];
        var (vm, _) = await NewsVmAsync();

        vm.PressMic(TalkInput.MicButton);
        Changes("a", SessionState.Working, SessionState.Idle);
        Changes("b", SessionState.Working, SessionState.Waiting, "Pick a branch");
        Changes("c", SessionState.Working, SessionState.Idle);
        Speak();
        _time.Advance(Hold);
        await vm.ReleaseMicAsync(TalkInput.MicButton);
        await WithinAsync(vm.PendingAnswers);
        DigestsAsked().ShouldBeEmpty("not while Raven answers");

        await GraceAsync(vm);
        await Until(() => _speech.Spoken.Count == 2);

        var digest = DigestsAsked().ShouldHaveSingleItem();
        digest.ShouldContain("- ContentAutomatorX, chat \"Fix the upload retry\": finished. It last said: \"All done.\"");
        digest.ShouldContain("- CodeSwitchX, chat \"Release notes\": needs you: \"Pick a branch\"");
        digest.ShouldContain("- DiffusionNexus, chat \"Speed up the loader\": finished");
        var card = vm.Log.Single(e => e.Kind == RavenLogKind.News);
        card.Lines!.Select(l => l.Text).ShouldBe([
            "ContentAutomatorX · Fix the upload retry: finished",
            "CodeSwitchX · Release notes: needs you",
            "DiffusionNexus · Speed up the loader: finished",
        ]);
        _speech.Spoken.ShouldBe(["Sure.", "ContentAutomatorX and DiffusionNexus are done, and CodeSwitchX wants you."]);
    }

    [Fact]
    public async Task Talking_during_the_digest_drops_the_rest_of_it()
    {
        _speech.Gate = new TaskCompletionSource();
        _brain.Answer = q => q.StartsWith(Digest, StringComparison.Ordinal)
            ? [new BrainText("ContentAutomatorX is done. CodeSwitchX wants you.")]
            : [new BrainText("Okay.")];
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
        DigestsAsked().Count.ShouldBe(1, "the dropped news is not told again");
        news.HasNews.ShouldBeFalse();
    }

    [Fact]
    public async Task A_chat_that_changes_twice_before_Raven_gets_to_it_is_told_once_with_its_latest_state()
    {
        _brain.Gate = new TaskCompletionSource();
        _brain.Answer = _ => [new BrainText("Fine.")];
        var (vm, _) = await NewsVmAsync();
        Type(vm, "How are things?");
        Changes("a", SessionState.Working, SessionState.Idle);
        Changes("a", SessionState.Idle, SessionState.Working);
        Changes("a", SessionState.Working, SessionState.Errored);
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);

        await GraceAsync(vm);

        var digest = DigestsAsked().ShouldHaveSingleItem();
        digest.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal)).ShouldBe(1);
        digest.ShouldContain("ContentAutomatorX, chat \"Fix the upload retry\": failed");
    }

    [Fact]
    public async Task News_waits_for_the_grace_after_the_floor_frees()
    {
        var (vm, _) = await NewsVmAsync();
        Changes("a", SessionState.Working, SessionState.Idle);

        _time.Advance(RavenPanelViewModel.NewsGrace - TimeSpan.FromMilliseconds(100));
        DigestsAsked().ShouldBeEmpty();
        _time.Advance(TimeSpan.FromMilliseconds(100));
        await WithinAsync(vm.PendingAnswers);

        DigestsAsked().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_brain_that_gives_no_words_says_the_plain_sentence()
    {
        _brain.Answer = _ => [];
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

        DigestsAsked().ShouldBeEmpty();
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
