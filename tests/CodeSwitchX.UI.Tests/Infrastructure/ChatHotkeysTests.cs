using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Infrastructure;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Infrastructure;

public class ChatHotkeysTests
{
    private static ChatHotkeyRow Row(ChatHotkeys keys, string id) => keys.Rows.Single(r => r.Id == id);

    [Fact]
    public void The_defaults_are_ctrl_alt_f12_for_chat_0_f1_to_f11_and_page_up_and_down()
    {
        var keys = new ChatHotkeys();

        keys.Rows.Select(r => r.Chord).ShouldBe([
            "Ctrl+Alt+F12", "Ctrl+Alt+F1", "Ctrl+Alt+F2", "Ctrl+Alt+F3", "Ctrl+Alt+F4", "Ctrl+Alt+F5", "Ctrl+Alt+F6", "Ctrl+Alt+F7",
            "Ctrl+Alt+F8", "Ctrl+Alt+F9", "Ctrl+Alt+F10", "Ctrl+Alt+F11", "Ctrl+Alt+PageUp", "Ctrl+Alt+PageDown",
            "Ctrl+Alt+End"]);
        keys.Rows.ShouldAllBe(r => r.Problem == null);
        keys.Active.Count.ShouldBe(15);
        Row(keys, "chat3").Target.ShouldBe(new ChatSwitch(3, false, false));
        Row(keys, "nextQuestion").ShouldSatisfyAllConditions(r => r.Target.ShouldBeNull(), r => r.Step.ShouldBe(0)); // #230: the next question
        Row(keys, "next").Step.ShouldBe(1);
    }

    [Fact]
    public void A_chord_set_twice_works_for_the_first_row_only_and_the_second_says_so()
    {
        var keys = new ChatHotkeys();
        var changes = 0;
        keys.Changed += () => changes++;

        Row(keys, "chat2").Chord = "Ctrl+Alt+F1";

        changes.ShouldBe(1);
        Row(keys, "chat2").Problem.ShouldBe("Chat 1 has Ctrl+Alt+F1 already.");
        keys.Active.ShouldNotContain(a => a.Row.Id == "chat2");
        keys.Active.ShouldContain(a => a.Row.Id == "chat1");
    }

    [Theory]
    [InlineData("Ctrl+Alt+3", "AltGr counts as Ctrl+Alt: this would stop AltGr+3 from typing in every app. Add Shift.")]
    [InlineData("Ctrl+Alt+Y", "CodeSwitchX uses Ctrl+Alt+Y already (Ctrl+Alt+Y).")]
    [InlineData("Ctrl+Alt+Space", "CodeSwitchX uses Ctrl+Alt+Space already (Push to talk).")]
    [InlineData("banana", "Not a key chord: click the box and press the keys.")]
    public void A_chord_that_cannot_work_says_why_and_is_not_registered(string chord, string problem)
    {
        var keys = new ChatHotkeys();

        Row(keys, "chat3").Chord = chord;

        Row(keys, "chat3").Problem.ShouldBe(problem);
        keys.Active.ShouldNotContain(a => a.Row.Id == "chat3");
    }

    [Fact]
    public void An_empty_chord_is_off_and_reset_brings_the_default_back()
    {
        var keys = new ChatHotkeys();

        Row(keys, "chat5").ClearCommand.Execute(null);
        Row(keys, "chat5").Problem.ShouldBeNull();
        keys.Active.ShouldNotContain(a => a.Row.Id == "chat5");

        Row(keys, "chat5").ResetCommand.Execute(null);
        Row(keys, "chat5").Chord.ShouldBe("Ctrl+Alt+F5");
    }

    [Fact]
    public async Task Chords_are_stored_and_read_back()
    {
        var store = Substitute.For<ISettingsStore>();
        Dictionary<string, string>? saved = null;
        store.SetAsync(ChatHotkeys.SettingKey, Arg.Do<Dictionary<string, string>>(d => saved = d), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var keys = new ChatHotkeys(store);

        Row(keys, "chat3").Chord = "Ctrl+Shift+Alt+3";

        saved.ShouldNotBeNull()["chat3"].ShouldBe("Ctrl+Shift+Alt+3");
        store.GetAsync<Dictionary<string, string>>(ChatHotkeys.SettingKey, Arg.Any<CancellationToken>()).Returns(saved);
        var again = new ChatHotkeys(store);
        await again.LoadAsync(TestContext.Current.CancellationToken);
        Row(again, "chat3").Chord.ShouldBe("Ctrl+Shift+Alt+3");
        Row(again, "chat4").Chord.ShouldBe("Ctrl+Alt+F4");
    }
}
