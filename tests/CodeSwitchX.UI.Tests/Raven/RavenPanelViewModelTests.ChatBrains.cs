using CodeSwitchX.Conductor;
using CodeSwitchX.UI.Raven;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>Each chat keeps its own conversation (#123): a question goes to the brain of the chat it is asked in.</summary>
public sealed partial class RavenPanelViewModelTests
{
    /// <summary>A brain per window; the Yard's is the panel's own <see cref="_brain"/>.</summary>
    private sealed class FakeChatBrains(FakeBrain yard) : IChatBrains
    {
        public Dictionary<Guid, FakeBrain> Windows { get; } = [];

        public IConductorBrain For(Guid? workspaceId) => workspaceId is { } id
            ? Windows.TryGetValue(id, out var brain) ? brain : Windows[id] = new FakeBrain { Answer = _ => [new BrainText("Window answer.")] }
            : yard;
    }

    private async Task<(RavenPanelViewModel Vm, FakeChatBrains Brains)> ChatBrainsVmAsync()
    {
        var brains = new FakeChatBrains(_brain);
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance, brains: brains);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX"), (ContentAutomatorX, 3, "ContentAutomatorX")]);
        return (vm, brains);
    }

    [Fact]
    public async Task What_is_said_in_a_chat_goes_only_to_that_chat_s_brain()
    {
        var (vm, brains) = await ChatBrainsVmAsync();
        _brain.Answer = _ => [new BrainText("Yard answer.")];

        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "Start a chat that fixes the retry");
        await WithinAsync(vm.PendingAnswers);
        vm.SelectedChat = ChatNumbered(vm, 1);
        Type(vm, "What is the branch here?");
        await WithinAsync(vm.PendingAnswers);
        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "What did I ask you before?");
        await WithinAsync(vm.PendingAnswers);
        vm.SelectedChat = vm.YardChat;
        Type(vm, "What needs me?");
        await WithinAsync(vm.PendingAnswers);

        brains.Windows[ContentAutomatorX].Sent.Count.ShouldBe(2);
        brains.Windows[ContentAutomatorX].Sent.ShouldAllBe(q => !q.Contains("branch here") && !q.Contains("needs me"));
        brains.Windows[ContentAutomatorX].Sent[1].ShouldContain("What did I ask you before?");
        brains.Windows[CodeSwitchX].Sent.ShouldHaveSingleItem().ShouldContain("What is the branch here?");
        _brain.Sent.ShouldHaveSingleItem().ShouldContain("What needs me?");
        vm.Log.Last(e => e.Kind == RavenLogKind.Raven).Text.ShouldBe("Yard answer.");
    }

    [Fact]
    public async Task An_answer_still_streams_from_the_brain_of_the_chat_it_was_asked_in()
    {
        var (vm, brains) = await ChatBrainsVmAsync();
        vm.SelectedChat = ChatNumbered(vm, 3);
        var three = brains.For(ContentAutomatorX) as FakeBrain;
        three!.Gate = new TaskCompletionSource();

        Type(vm, "Is the retry test green?");
        vm.SelectedChat = ChatNumbered(vm, 1);
        three.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);

        vm.Log.Single(e => e.Kind == RavenLogKind.Raven).Chat.ShouldBe(ChatNumbered(vm, 3));
        brains.Windows.ContainsKey(CodeSwitchX).ShouldBeFalse("chat 1 was not asked anything");
    }

    [Fact]
    public async Task Talking_warms_up_the_brain_of_the_chat_the_user_is_in()
    {
        var (vm, brains) = await ChatBrainsVmAsync();
        vm.SelectedChat = ChatNumbered(vm, 3);

        await HoldAsync(vm);

        ((FakeBrain)brains.For(ContentAutomatorX)).WarmUps.ShouldBe(1);
        _brain.WarmUps.ShouldBe(0);
    }
}
