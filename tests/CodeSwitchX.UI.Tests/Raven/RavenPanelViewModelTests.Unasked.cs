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

        unasked.Report(new UnaskedTurn(ContentAutomatorX, "The bug report chat asks which F keys fail."));

        var entry = vm.Log.ShouldHaveSingleItem();
        entry.Kind.ShouldBe(RavenLogKind.Raven);
        entry.Text.ShouldBe("The bug report chat asks which F keys fail.");
        entry.Chat.ShouldBe(ChatNumbered(vm, 3));
        entry.Said.ShouldBeFalse("it comes unasked: only written");
        ChatNumbered(vm, 3).Unread.ShouldBe(1, "the user is in chat 1 and has not seen it");
        _speech.Spoken.ShouldBeEmpty();
    }

    [Fact]
    public async Task What_chat_0_s_brain_says_in_a_turn_of_its_own_is_written_in_the_yard()
    {
        var (vm, unasked) = await UnaskedVmAsync();

        unasked.Report(new UnaskedTurn(null, "A chat sent Raven a message."));

        vm.Log.ShouldHaveSingleItem().Chat.ShouldBe(vm.YardChat);
    }
}
