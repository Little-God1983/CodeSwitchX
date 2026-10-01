using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.UI.Tests.Infrastructure;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>The Push to talk | Open mic switch: a choice by click or UI Automation, and a warning that is given once.</summary>
public sealed partial class RavenPanelViewModelTests
{
    // Review of #89: an unavailable Open mic warns once
    [Fact]
    public async Task Choosing_Open_mic_without_it_warns_once_and_stays_in_push_to_talk()
    {
        var vm = await NewVmAsync();

        vm.ChooseMicModeCommand.Execute(MicMode.OpenMic);

        vm.Log.Count(e => e.Kind == RavenLogKind.Warning && e.Text == "Open mic is not available.").ShouldBe(1);
        vm.MicMode.ShouldBe(MicMode.PushToTalk);
        vm.PreferredMicMode.ShouldBe(MicMode.OpenMic);
    }

    /// <summary>The two halves as the view has them: radio buttons whose IsChecked follows the panel's MicMode one way, like the binding.</summary>
    private static (RadioButton PushToTalk, RadioButton OpenMic) NewSwitch(RavenPanelViewModel vm)
    {
        var modeSwitch = new MicModeSwitch(() => vm);
        RadioButton Half(MicMode mode)
        {
            var half = new RadioButton { Tag = mode, IsChecked = vm.MicMode == mode };
            half.Checked += (sender, _) => modeSwitch.OnChecked(sender);
            half.Click += (sender, _) => modeSwitch.OnClicked(sender);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(RavenPanelViewModel.MicMode))
                {
                    half.IsChecked = vm.MicMode == mode;
                }
            };
            return half;
        }

        return (Half(MicMode.PushToTalk), Half(MicMode.OpenMic));
    }

    private static void Select(RadioButton half) =>
        ((ISelectionItemProvider)new RadioButtonAutomationPeer(half).GetPattern(PatternInterface.SelectionItem)).Select();

    private static void Click(RadioButton half) => half.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    // Review of #89: UI Automation's Select raises no click
    [Fact]
    public async Task Selecting_a_half_of_the_mode_switch_through_UI_Automation_chooses_that_mode()
    {
        var vm = await NewOpenMicVmAsync();

        await StaThread.RunAsync(() =>
        {
            var (pushToTalk, openMic) = NewSwitch(vm);

            Select(openMic);
            vm.MicMode.ShouldBe(MicMode.OpenMic);
            vm.PreferredMicMode.ShouldBe(MicMode.OpenMic);

            Select(pushToTalk);
            vm.MicMode.ShouldBe(MicMode.PushToTalk);
            vm.PreferredMicMode.ShouldBe(MicMode.PushToTalk);
            pushToTalk.IsChecked.ShouldBe(true);
        });
    }

    [Fact]
    public async Task The_panel_changing_its_own_mode_is_no_choice_of_the_user()
    {
        var vm = await NewOpenMicVmAsync();

        await StaThread.RunAsync(() =>
        {
            var (_, openMic) = NewSwitch(vm);

            vm.MicMode = MicMode.OpenMic; // as a fallback or a restored state does
            openMic.IsChecked.ShouldBe(true);
            vm.PreferredMicMode.ShouldBe(MicMode.PushToTalk);
        });
    }

    [Fact]
    public async Task A_click_is_chosen_once_and_one_on_the_half_already_checked_still_counts()
    {
        var vm = await NewVmAsync(); // no Open mic: choosing it warns, falls back, and keeps the choice

        await StaThread.RunAsync(() =>
        {
            var (pushToTalk, openMic) = NewSwitch(vm);

            openMic.IsChecked = true; // what a click does first: the check, then the click
            Click(openMic);
            vm.Log.Count(e => e.Kind == RavenLogKind.Warning).ShouldBe(1);
            vm.PreferredMicMode.ShouldBe(MicMode.OpenMic);
            vm.MicMode.ShouldBe(MicMode.PushToTalk);

            Click(pushToTalk); // checked already, after the fallback
            vm.PreferredMicMode.ShouldBe(MicMode.PushToTalk);
        });
    }
}
