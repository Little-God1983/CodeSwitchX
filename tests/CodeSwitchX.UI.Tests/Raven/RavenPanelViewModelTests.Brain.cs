using CodeSwitchX.Conductor;
using CodeSwitchX.UI.Raven;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>What Raven answers: each question goes to its brain, and the log shows the reply and what it looked at.</summary>
public sealed partial class RavenPanelViewModelTests
{
    private static BrainToolCall ListChats(string id = "t1") => new(id, "list_chats", """{"filter":"needs_me"}""");

    private async Task<RavenPanelViewModel> AskedAsync(string question)
    {
        var vm = await NewVmAsync();
        Type(vm, question);
        await WithinAsync(vm.PendingAnswers);
        return vm;
    }

    private static void Type(RavenPanelViewModel vm, string text)
    {
        vm.TypedText = text;
        vm.SubmitTypedCommand.Execute(null);
    }

    private static IEnumerable<(RavenLogKind Kind, string Text)> Lines(RavenPanelViewModel vm) => vm.Log.Select(e => (e.Kind, e.Text));

    [Fact]
    public async Task A_typed_question_is_answered_in_one_entry_that_grows_as_the_reply_streams_in()
    {
        _brain.Answer = _ => [new BrainText("You have"), new BrainText(" one chat waiting.")];

        var vm = await AskedAsync("What's waiting on me?");

        _brain.Asked.ShouldBe(["What's waiting on me?"]);
        Lines(vm).ShouldBe([(RavenLogKind.You, "What's waiting on me?"), (RavenLogKind.Raven, "You have one chat waiting.")]);
    }

    [Fact]
    public async Task A_spoken_question_is_asked_once_it_is_transcribed()
    {
        var vm = await NewVmAsync();

        await HoldAsync(vm);
        await WithinAsync(vm.PendingTranscriptions);
        await WithinAsync(vm.PendingAnswers);

        _brain.Asked.ShouldBe(["Hallo Raven, open Diffusion Nexus"]);
    }

    [Fact]
    public async Task What_it_looked_at_shows_as_a_card_between_the_parts_of_the_reply()
    {
        _brain.Answer = _ => [new BrainText("Let me look."), ListChats(), new BrainToolResult("t1", false), new BrainText("One chat.")];

        var vm = await AskedAsync("What's waiting on me?");

        Lines(vm).ShouldBe([
            (RavenLogKind.You, "What's waiting on me?"),
            (RavenLogKind.Raven, "Let me look."),
            (RavenLogKind.Action, "list_chats"),
            (RavenLogKind.Raven, "One chat."),
        ]);
        vm.Log[2].Detail.ShouldBe("needs_me");
        vm.Log[2].Failed.ShouldBeFalse();
    }

    [Fact]
    public async Task A_tool_call_that_failed_marks_its_card()
    {
        _brain.Answer = _ => [ListChats("a"), new BrainToolCall("b", "list_workspaces", "{}"), new BrainToolResult("a", true), new BrainToolResult("b", false)];

        var vm = await AskedAsync("Which chats?");

        var cards = vm.Log.Where(e => e.Kind == RavenLogKind.Action).ToList();
        cards.Select(c => (c.Text, c.Detail, c.Failed)).ShouldBe([("list_chats", "needs_me", true), ("list_workspaces", null, false)]);
    }

    [Fact]
    public async Task The_reply_loses_the_space_it_starts_and_ends_with()
    {
        _brain.Answer = _ => [new BrainText("  "), new BrainText("\n Hi"), new BrainText(" there.\n")];

        var vm = await AskedAsync("Hi");

        vm.Log[^1].Text.ShouldBe("Hi there.");
    }

    [Fact]
    public async Task A_reply_of_nothing_adds_no_entry()
    {
        _brain.Answer = _ => [new BrainText("  ")];

        var vm = await AskedAsync("Hi");

        vm.Log.ShouldHaveSingleItem().Kind.ShouldBe(RavenLogKind.You);
    }

    [Fact]
    public async Task What_the_brain_says_about_itself_and_why_it_could_not_answer_reach_the_log()
    {
        _brain.Answer = _ =>
        [
            new BrainNotice("Raven's brain stopped and was started again.", Warning: false),
            new BrainNotice("Raven cannot see the Yard.", Warning: true),
            new BrainFailed("Raven's brain could not answer: API Error: 529 Overloaded"),
        ];

        var vm = await AskedAsync("Hi");

        Lines(vm).Skip(1).ShouldBe([
            (RavenLogKind.Note, "Raven's brain stopped and was started again."),
            (RavenLogKind.Warning, "Raven cannot see the Yard."),
            (RavenLogKind.Warning, "Raven's brain could not answer: API Error: 529 Overloaded"),
        ]);
    }

    [Fact]
    public async Task A_brain_that_throws_is_a_warning_and_the_next_question_is_still_asked()
    {
        var vm = await NewVmAsync();
        _brain.Answer = _ => throw new InvalidOperationException("boom");
        Type(vm, "One");
        await WithinAsync(vm.PendingAnswers);

        _brain.Answer = _ => [new BrainText("Two.")];
        Type(vm, "Two");
        await WithinAsync(vm.PendingAnswers);

        vm.Log.ShouldContain(e => e.Kind == RavenLogKind.Warning && e.Text == "Raven could not answer: boom");
        vm.Log[^1].Text.ShouldBe("Two.");
        vm.State.ShouldBe(RavenState.Idle);
    }

    [Fact]
    public async Task The_panel_thinks_until_the_answer_is_in()
    {
        var vm = await NewVmAsync();
        _brain.Gate = new TaskCompletionSource();
        _brain.Answer = _ => [new BrainText("Done.")];

        Type(vm, "What's waiting on me?");

        vm.State.ShouldBe(RavenState.Thinking);
        vm.Caption.ShouldBe("Thinking…");
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);
        vm.State.ShouldBe(RavenState.Idle);
        vm.Caption.ShouldBe(RavenPanelViewModel.IdleCaption);
    }

    [Fact]
    public async Task Questions_asked_while_one_is_answered_wait_their_turn_and_are_answered_in_order()
    {
        var vm = await NewVmAsync();
        var gate = new TaskCompletionSource();
        _brain.Gate = gate;
        _brain.Answer = question => [new BrainText($"Answer to {question}.")];

        Type(vm, "one");
        Type(vm, "two");

        vm.Caption.ShouldBe("Thinking… (1 waiting)");
        _brain.Asked.ShouldBe(["one"], "the second waits for the first to be answered");
        gate.SetResult();
        await WithinAsync(vm.PendingAnswers);
        Lines(vm).ShouldBe([
            (RavenLogKind.You, "one"),
            (RavenLogKind.You, "two"),
            (RavenLogKind.Raven, "Answer to one."),
            (RavenLogKind.Raven, "Answer to two."),
        ]);
    }

    [Fact]
    public async Task The_mic_listens_while_Raven_thinks()
    {
        var vm = await NewVmAsync();
        _brain.Gate = new TaskCompletionSource();
        Type(vm, "What's waiting on me?");

        vm.PressMic(TalkInput.MicButton);

        vm.State.ShouldBe(RavenState.Listening);
        await WithinAsync(vm.ReleaseMicAsync(TalkInput.MicButton));
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);
    }

    [Fact]
    public async Task Pressing_the_mic_gets_the_brain_ready_while_the_user_talks()
    {
        var vm = await NewVmAsync();

        await HoldAsync(vm);

        _brain.WarmUps.ShouldBe(1);
    }

    [Theory]
    [InlineData("""{"filter":"needs_me","workspace":"Diffusion-Full"}""", "needs_me, Diffusion-Full")]
    [InlineData("""{"id":"bbbbbbbb","limit":3,"all":true}""", "bbbbbbbb, 3, true")]
    [InlineData("""{"workspace":""}""", null)]
    [InlineData("{}", null)]
    [InlineData("[1]", null)]
    [InlineData("not json", null)]
    public void A_card_shows_the_values_the_tool_was_called_with(string input, string? detail)
    {
        RavenPanelViewModel.ActionDetail(input).ShouldBe(detail);
    }
}
