using CodeSwitchX.Conductor;
using CodeSwitchX.UI.Raven;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>What a brain says in a turn of its own, started by a chat's message to it (#181), is shown in its Raven chat.</summary>
public sealed partial class RavenPanelViewModelTests
{
    private async Task<(RavenPanelViewModel Vm, UnaskedTurns Unasked)> UnaskedVmAsync()
    {
        var unasked = new UnaskedTurns();
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance, brains: new FakeChatBrains(_brain), unasked: unasked);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX"), (ContentAutomatorX, 3, "ContentAutomatorX")]);
        return (vm, unasked);
    }

    [Fact]
    public async Task What_a_window_s_brain_says_in_a_turn_of_its_own_is_written_in_that_window_s_chat_and_not_said()
    {
        var (vm, unasked) = await UnaskedVmAsync();
        vm.SelectedChat = ChatNumbered(vm, 1);

        unasked.Report(new UnaskedTurn(ContentAutomatorX, "The bug report chat asks which F keys fail.", [], null));

        var entry = vm.Log.ShouldHaveSingleItem();
        entry.Kind.ShouldBe(RavenLogKind.Raven);
        entry.Text.ShouldBe("The bug report chat asks which F keys fail.");
        entry.Chat.ShouldBe(ChatNumbered(vm, 3));
        entry.Said.ShouldBeFalse("it comes unasked: only written");
        ChatNumbered(vm, 3).Unread.ShouldBe(1, "the user is in chat 1 and has not seen it");
        _speech.Spoken.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_tool_a_brain_called_in_a_turn_of_its_own_gets_its_card_in_that_window_s_chat()
    {
        var (vm, unasked) = await UnaskedVmAsync();

        unasked.Report(new UnaskedTurn(ContentAutomatorX, "", [new UnaskedCall(new BrainToolCall("toolu_9", "stop_chat", """{"chat":"issues"}"""), Failed: true)], null));

        var card = vm.Log.ShouldHaveSingleItem("it said nothing: no empty line");
        (card.Kind, card.Text, card.Chat).ShouldBe((RavenLogKind.Action, "stop_chat", ChatNumbered(vm, 3)));
        card.Detail.ShouldNotBeNull();
        card.Failed.ShouldBeTrue("the tool failed: the card says so, as in an answer");
    }

    [Fact]
    public async Task A_turn_of_its_own_that_failed_is_a_warning_after_what_it_said_so_far()
    {
        var (vm, unasked) = await UnaskedVmAsync();

        unasked.Report(new UnaskedTurn(ContentAutomatorX, "I've told the issues chat to", [], "API Error: 529 Overloaded"));

        vm.Log.Select(e => (e.Kind, e.Text)).ShouldBe([
            (RavenLogKind.Raven, "I've told the issues chat to"),
            (RavenLogKind.Warning, "Raven could not answer a message from another chat: API Error: 529 Overloaded."),
        ]);
        vm.Log.ShouldAllBe(e => e.Chat == ChatNumbered(vm, 3));
    }

    [Fact]
    public async Task What_chat_0_s_brain_says_in_a_turn_of_its_own_is_written_in_the_yard()
    {
        var (vm, unasked) = await UnaskedVmAsync();

        unasked.Report(new UnaskedTurn(null, "A chat sent Raven a message.", [], null));

        vm.Log.ShouldHaveSingleItem().Chat.ShouldBe(vm.YardChat);
    }
}
