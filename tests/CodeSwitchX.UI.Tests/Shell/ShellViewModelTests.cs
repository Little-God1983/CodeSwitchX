using CodeSwitchX.Core;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.Ingest.Hooks;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.UI.Settings;
using CodeSwitchX.UI.Shell;
using CodeSwitchX.Voice.Audio;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Shell;

public class ShellViewModelTests
{
    private const nint ShopHwnd = 700;
    private readonly ShellTestHarness _h = new();

    /// <summary>A second workspace whose VS Code window is open already, so opening it adopts that window. App's window never appears: its open ends Stopped.</summary>
    private Workspace AddShopWithItsWindowOpen()
    {
        var shop = new Workspace { Name = "Shop", RootPath = @"c:\repo\shop", TrackId = _h.General.Id };
        _h.Workspaces.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([_h.App, shop]));
        _h.Windows.TopLevelWindows().Returns([new WindowInfo(ShopHwnd, 31, "Chrome_WidgetWin_1", "Program.cs - Shop - Visual Studio Code")]);
        _h.Launcher.Launch(Arg.Any<Workspace>()).Returns(new LaunchResult(true, 1, null));
        return shop;
    }

    [Fact]
    public async Task The_yard_starts_at_the_stored_tile_size_and_a_change_is_stored_without_one_at_startup()
    {
        _h.Settings.GetAsync<double?>(SettingKeys.TileScale, Arg.Any<CancellationToken>()).Returns(Task.FromResult<double?>(1.25));

        await _h.Shell.InitializeAsync(CancellationToken.None);

        _h.Shell.Yard.TileScale.ShouldBe(1.25);
        await _h.Shell.Settings.FlushSavesAsync(CancellationToken.None);
        await _h.Settings.DidNotReceive().SetAsync(SettingKeys.TileScale, Arg.Any<double>(), Arg.Any<CancellationToken>());

        _h.Shell.Yard.TileScale = 0.9000000000000001;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _h.Shell.Settings.FlushSavesAsync(timeout.Token);

        await _h.Settings.Received(1).SetAsync(SettingKeys.TileScale, 0.9, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_raven_panel_starts_in_its_stored_state()
    {
        _h.Settings.GetAsync<bool?>(SettingKeys.RavenPanelOpen, Arg.Any<CancellationToken>()).Returns(Task.FromResult<bool?>(false));

        await _h.Shell.InitializeAsync(CancellationToken.None);

        _h.Shell.Raven.IsOpen.ShouldBeFalse();
        await _h.Shell.Settings.FlushSavesAsync(CancellationToken.None);
        await _h.Settings.DidNotReceive().SetAsync(SettingKeys.RavenPanelOpen, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Collapsing_raven_is_saved()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Shell.Raven.IsOpen.ShouldBeTrue("a panel never collapsed starts open");

        _h.Shell.Raven.TogglePanelCommand.Execute(null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _h.Shell.Settings.FlushSavesAsync(timeout.Token);

        _h.Shell.Settings.RavenPanelOpen.ShouldBeFalse();
        await _h.Settings.Received(1).SetAsync(SettingKeys.RavenPanelOpen, false, Arg.Any<CancellationToken>());
    }

    // A slow Bluetooth or USB endpoint makes the enumeration take seconds: the shell comes up without waiting for it.
    [Fact]
    public async Task Startup_does_not_wait_for_the_microphones_to_be_listed()
    {
        var headset = new MicrophoneDevice("id-headset", "Headset");
        using var hold = new ManualResetEventSlim();
        _h.Microphones.List().Returns(_ =>
        {
            hold.Wait(TimeSpan.FromSeconds(10));
            return [headset];
        });
        _h.Microphones.Default().Returns(headset);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await _h.Shell.InitializeAsync(CancellationToken.None);

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
        _h.Shell.Raven.SelectedMicrophone.ShouldBeNull();
        hold.Set();
        await _h.Shell.Raven.PendingRefresh.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _h.Shell.Raven.SelectedMicrophone.ShouldBe(headset);
    }

    [Fact]
    public async Task A_stored_microphone_that_is_still_plugged_in_is_selected_at_startup()
    {
        var headset = new MicrophoneDevice("id-headset", "Headset");
        var desk = new MicrophoneDevice("id-desk", "Desk mic");
        _h.Microphones.List().Returns([headset, desk]);
        _h.Microphones.Default().Returns(headset);
        _h.Settings.GetAsync<MicrophoneDevice>(SettingKeys.RavenMicrophone, Arg.Any<CancellationToken>()).Returns(Task.FromResult<MicrophoneDevice?>(desk));

        await _h.Shell.InitializeAsync(CancellationToken.None);
        await _h.Shell.Raven.PendingRefresh; // the microphones are listed off the UI thread

        _h.Shell.Raven.Microphones.ShouldBe([headset, desk]);
        _h.Shell.Raven.SelectedMicrophone.ShouldBe(desk);
        _h.Shell.Raven.Log.ShouldBeEmpty("nothing fell back");
        await _h.Shell.Settings.FlushSavesAsync(CancellationToken.None);
        await _h.Settings.DidNotReceive().SetAsync(SettingKeys.RavenMicrophone, Arg.Any<MicrophoneDevice?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_stored_microphone_that_appears_after_startup_is_selected_and_never_saved_over()
    {
        // CodeSwitchX started before RØDE Connect: the virtual input is missing at first.
        var headset = new MicrophoneDevice("id-headset", "Headset");
        var rode = new MicrophoneDevice("id-rode", "RØDE Connect Virtual Input");
        _h.Microphones.List().Returns([headset]);
        _h.Microphones.Default().Returns(headset);
        _h.Settings.GetAsync<MicrophoneDevice>(SettingKeys.RavenMicrophone, Arg.Any<CancellationToken>()).Returns(Task.FromResult<MicrophoneDevice?>(rode));
        await _h.Shell.InitializeAsync(CancellationToken.None);
        await _h.Shell.Raven.PendingRefresh; // the microphones are listed off the UI thread
        _h.Shell.Raven.SelectedMicrophone.ShouldBe(headset);

        _h.Microphones.DevicesChanged += Raise.Event<EventHandler>(_h.Microphones, EventArgs.Empty); // something else changed
        _h.Time.Advance(RavenPanelViewModel.DeviceChangeSettle);
        _h.Microphones.List().Returns([headset, rode]);
        _h.Microphones.DevicesChanged += Raise.Event<EventHandler>(_h.Microphones, EventArgs.Empty);
        _h.Time.Advance(RavenPanelViewModel.DeviceChangeSettle);

        _h.Shell.Raven.SelectedMicrophone.ShouldBe(rode);
        _h.Shell.Settings.RavenMicrophone.ShouldBe(rode);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _h.Shell.Settings.FlushSavesAsync(timeout.Token);
        await _h.Settings.DidNotReceive().SetAsync(SettingKeys.RavenMicrophone, Arg.Any<MicrophoneDevice?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unplugging_the_chosen_microphone_uses_the_default_without_saving_it()
    {
        var headset = new MicrophoneDevice("id-headset", "Headset");
        var desk = new MicrophoneDevice("id-desk", "Desk mic");
        _h.Microphones.List().Returns([headset, desk]);
        _h.Microphones.Default().Returns(headset);
        _h.Settings.GetAsync<MicrophoneDevice>(SettingKeys.RavenMicrophone, Arg.Any<CancellationToken>()).Returns(Task.FromResult<MicrophoneDevice?>(desk));
        await _h.Shell.InitializeAsync(CancellationToken.None);
        await _h.Shell.Raven.PendingRefresh; // the microphones are listed off the UI thread

        _h.Microphones.List().Returns([headset]);
        _h.Microphones.DevicesChanged += Raise.Event<EventHandler>(_h.Microphones, EventArgs.Empty);
        _h.Time.Advance(RavenPanelViewModel.DeviceChangeSettle);

        _h.Shell.Raven.SelectedMicrophone.ShouldBe(headset);
        _h.Shell.Settings.RavenMicrophone.ShouldBe(desk);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _h.Shell.Settings.FlushSavesAsync(timeout.Token);
        await _h.Settings.DidNotReceive().SetAsync(SettingKeys.RavenMicrophone, Arg.Any<MicrophoneDevice?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Choosing_another_microphone_is_saved()
    {
        var headset = new MicrophoneDevice("id-headset", "Headset");
        var desk = new MicrophoneDevice("id-desk", "Desk mic");
        _h.Microphones.List().Returns([headset, desk]);
        _h.Microphones.Default().Returns(headset);
        await _h.Shell.InitializeAsync(CancellationToken.None);
        await _h.Shell.Raven.PendingRefresh; // the microphones are listed off the UI thread
        _h.Shell.Raven.SelectedMicrophone.ShouldBe(headset, "nothing stored: the Windows default");

        _h.Shell.Raven.SelectedMicrophone = desk;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _h.Shell.Settings.FlushSavesAsync(timeout.Token);

        await _h.Settings.Received(1).SetAsync(SettingKeys.RavenMicrophone, desk, Arg.Any<CancellationToken>());
    }

    // The first clip after launch pays for loading the model unless something loads it first. Five seconds leave the
    // startup itself alone.
    [Fact]
    public async Task The_speech_model_is_warmed_up_a_few_seconds_after_startup()
    {
        _h.Models.IsPresent.Returns(true);
        var warmed = new TaskCompletionSource();
        _h.Dictation.WarmUpAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            warmed.TrySetResult();
            return Task.CompletedTask;
        });
        await _h.Shell.InitializeAsync(CancellationToken.None);
        warmed.Task.IsCompleted.ShouldBeFalse();

        _h.Time.Advance(TimeSpan.FromSeconds(5));

        await warmed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void The_window_title_names_the_build()
    {
        // The only place that tells a stable build from a Debug build on screen; what the version reads is AppVersionTests' business.
        _h.Shell.Title.ShouldStartWith("CodeSwitchX ");
        _h.Shell.Title.Length.ShouldBeGreaterThan("CodeSwitchX ".Length, "the version follows");
    }

    [Fact]
    public async Task Raven_takes_a_chat_s_question_while_its_panel_is_open_unless_the_Cab_shows_that_chat_s_VS_Code()
    {
        var other = Guid.NewGuid();
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Shell.TakesAsks(_h.App.Id).ShouldBeTrue();
        _h.Shell.TakesAsks(null).ShouldBeTrue("a chat on no tile is shown in no Cab");

        _h.VsCodeWindowAppears();
        _h.Shell.Cab.LastHostRect = ScreenRect.FromSize(0, 28, 1600, 900);
        await _h.Shell.EnterCabAsync(_h.App.Id);

        _h.Shell.TakesAsks(_h.App.Id).ShouldBeFalse("the user is looking at that chat's VS Code: its tab asks");
        _h.Shell.TakesAsks(other).ShouldBeTrue();

        _h.Shell.SetShellMinimized(true);
        _h.Shell.TakesAsks(_h.App.Id).ShouldBeTrue("minimised, the Cab shows nothing");
        _h.Shell.SetShellMinimized(false);

        _h.Shell.BackToYard();
        _h.Shell.TakesAsks(_h.App.Id).ShouldBeTrue();

        _h.Shell.Raven.TogglePanelCommand.Execute(null);
        _h.Shell.TakesAsks(other).ShouldBeFalse("with the panel collapsed every question goes to VS Code");
    }

    [Fact]
    public async Task A_question_held_lets_go_once_the_Cab_shows_its_chat_s_VS_Code_and_a_collapsed_panel_keeps_it()
    {
        var other = Guid.NewGuid();
        await _h.Shell.InitializeAsync(CancellationToken.None);
        var changes = 0;
        _h.Shell.AskRulesChanged += (_, _) => changes++;
        _h.Shell.Raven.TogglePanelCommand.Execute(null);
        _h.Shell.KeepsAsks(_h.App.Id).ShouldBeTrue("the rail counts it");

        _h.VsCodeWindowAppears();
        _h.Shell.Cab.LastHostRect = ScreenRect.FromSize(0, 28, 1600, 900);
        await _h.Shell.EnterCabAsync(_h.App.Id);

        changes.ShouldBeGreaterThan(0);
        _h.Shell.KeepsAsks(_h.App.Id).ShouldBeFalse("the user is looking at that chat's tab");
        _h.Shell.KeepsAsks(other).ShouldBeTrue();
        _h.Shell.KeepsAsks(null).ShouldBeTrue();

        var seen = changes;
        _h.Shell.SetShellMinimized(true);
        _h.Shell.KeepsAsks(_h.App.Id).ShouldBeTrue("push-to-talk answers what Raven reads out");
        changes.ShouldBe(seen + 1);
    }

    [Fact]
    public void Before_the_shell_is_set_up_it_takes_no_question()
    {
        _h.Shell.TakesAsks(_h.App.Id).ShouldBeFalse();
    }

    [Fact]
    public async Task EnterCab_switches_mode_opens_vscode_and_docks_into_the_known_rect()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        var rect = ScreenRect.FromSize(0, 28, 1600, 900);
        _h.Shell.Cab.LastHostRect = rect;

        await _h.Shell.EnterCabAsync(_h.App.Id);

        _h.Shell.Mode.ShouldBe(ShellMode.Cab);
        _h.Shell.ActiveWorkspaceId.ShouldBe(_h.App.Id);
        _h.Shell.Cab.ActiveTile!.Id.ShouldBe(_h.App.Id);
        _h.Host.Get(_h.App.Id)!.State.ShouldBe(HostState.Running);
        _h.Docker.Received(2).MoveTo(500, rect); // placed in the Cab as it appeared, then docked there
        _h.Docker.DidNotReceive().Cloak(500);
        _h.Shell.StatusMessage.ShouldBeNull();
    }

    /// <summary>Raven's list has a chat per tile; the Cab opening a window selects its chat and folds the list, the Yard unfolds it.</summary>
    [Fact]
    public async Task Opening_a_window_in_the_cab_selects_its_raven_chat_and_folds_the_list()
    {
        _h.App.Number = 2;
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Shell.Raven.Chats.Select(c => c.Label).ShouldBe(["0 Yard", "2 App", "Activity"]);
        _h.VsCodeWindowAppears();

        await _h.Shell.EnterCabAsync(_h.App.Id);

        _h.Shell.Raven.SelectedChat.Label.ShouldBe("2 App");
        _h.Shell.Raven.IsListFolded.ShouldBeTrue();
        _h.Shell.BackToYardCommand.Execute(null);
        _h.Shell.Raven.IsListFolded.ShouldBeFalse();
        _h.Shell.Raven.SelectedChat.Label.ShouldBe("2 App", "going back chooses no other chat");
    }

    /// <summary>"Open chat two" switches Raven's chat and docks its window; the brain's switch_chat gets Raven's line back.</summary>
    [Fact]
    public async Task Open_chat_two_docks_its_window_and_the_brain_s_switch_says_where()
    {
        _h.App.Number = 2;
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        var forwarded = false;
        _h.Shell.ForwardRequested += () => forwarded = true;

        _h.Shell.Raven.SwitchChat(new CodeSwitchX.Core.Yard.ChatSwitch(2, false, Open: true));
        _h.Shell.Mode.ShouldBe(ShellMode.Cab); // set before the Cab waits for VS Code

        _h.Shell.ActiveWorkspaceId.ShouldBe(_h.App.Id);
        forwarded.ShouldBeTrue("the window comes forward, as when the brain opens a workspace");
        IRavenShell shell = _h.Shell;
        shell.SwitchChat(new CodeSwitchX.Core.Yard.ChatSwitch(0, false, false))!.Value.Said.ShouldBe("Chat 0, the Yard.");
        shell.SwitchChat(new CodeSwitchX.Core.Yard.ChatSwitch(9, false, false)).ShouldBeNull();
    }

    [Fact]
    public async Task Choosing_a_raven_chat_does_not_open_its_window()
    {
        _h.App.Number = 2;
        await _h.Shell.InitializeAsync(CancellationToken.None);

        _h.Shell.Raven.SelectedChat = _h.Shell.Raven.Chats[1];

        _h.Shell.Mode.ShouldBe(ShellMode.Yard);
        _h.Launcher.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Going_back_to_the_yard_while_a_new_window_waits_in_the_cab_puts_it_back_and_hides_it_once_known()
    {
        // The window stood in the Cab's area over the Yard, and once its title came it was adopted as shown and stayed there.
        await _h.Shell.InitializeAsync(CancellationToken.None);
        var rect = ScreenRect.FromSize(0, 28, 1600, 900);
        var whereVsCodePutIt = ScreenRect.FromSize(-2000, 100, 1800, 1100);
        _h.Shell.Cab.LastHostRect = rect;
        IReadOnlyList<WindowInfo> listing = [];
        _h.Windows.TopLevelWindows().Returns(_ => listing);
        _h.Windows.Describe(500).Returns(new WindowInfo(500, 30, "Chrome_WidgetWin_1", "Visual Studio Code"));
        _h.Docker.GetRect(500).Returns(whereVsCodePutIt);
        var launched = new TaskCompletionSource();
        _h.Launcher.Launch(Arg.Any<Workspace>()).Returns(_ => { launched.TrySetResult(); return new LaunchResult(true, 1, null); });

        var enter = _h.Shell.EnterCabAsync(_h.App.Id);
        await launched.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _h.Host.WindowAppeared(500);
        _h.Docker.Received(1).MoveTo(500, rect);

        _h.Shell.BackToYard();
        listing = [new WindowInfo(500, 30, "Chrome_WidgetWin_1", "Program.cs - app - Visual Studio Code")];
        await enter;

        _h.Shell.Mode.ShouldBe(ShellMode.Yard);
        _h.Host.Get(_h.App.Id)!.State.ShouldBe(HostState.Running);
        _h.Docker.Received(1).MoveTo(500, whereVsCodePutIt);
        _h.Docker.Received(1).Cloak(500);
        _h.Host.Get(_h.App.Id)!.Visible.ShouldBeFalse();
        _h.Docker.Received(1).MoveTo(500, rect);
    }

    [Fact]
    public async Task EnterCab_reports_launch_problems_in_the_status_message()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Windows.TopLevelWindows().Returns([]);
        _h.Launcher.Launch(Arg.Any<CodeSwitchX.Core.Workspaces.Workspace>()).Returns(new CodeSwitchX.Hosting.VsCode.LaunchResult(false, null, "code not found"));

        await _h.Shell.EnterCabAsync(_h.App.Id);

        _h.Shell.Mode.ShouldBe(ShellMode.Cab);
        _h.Shell.StatusMessage.ShouldBe("code not found");
    }

    [Fact]
    public async Task BackToYard_hides_windows_and_ToggleMode_round_trips()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        _h.Shell.Cab.LastHostRect = ScreenRect.FromSize(0, 0, 100, 100);
        await _h.Shell.EnterCabAsync(_h.App.Id);
        _h.Docker.ClearReceivedCalls(); // discovery cloaks once; only the Back cloak counts below

        _h.Shell.BackToYard();
        _h.Shell.Mode.ShouldBe(ShellMode.Yard);
        _h.Docker.Received(1).Cloak(500);

        _h.Shell.ToggleMode();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        _h.Shell.Mode.ShouldBe(ShellMode.Cab);
    }

    [Fact]
    public async Task UpdateCabRect_redocks_only_while_in_cab_mode()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        await _h.Shell.EnterCabAsync(_h.App.Id);
        var rect = ScreenRect.FromSize(10, 40, 800, 600);

        _h.Shell.UpdateCabRect(rect);
        _h.Docker.Received(1).MoveTo(500, rect);

        _h.Shell.BackToYard();
        _h.Shell.UpdateCabRect(ScreenRect.FromSize(0, 0, 50, 50));
        _h.Docker.DidNotReceive().MoveTo(500, ScreenRect.FromSize(0, 0, 50, 50));
    }

    /// <summary>Ctrl+Shift+Alt+N opens workspace number N, not the N-th tile: the numbers stay put when tiles move.</summary>
    [Fact]
    public async Task JumpTo_opens_the_workspace_with_that_number_and_ignores_a_number_none_has()
    {
        _h.App.Number = 3;
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();

        await _h.Shell.JumpToAsync(1);
        _h.Shell.Mode.ShouldBe(ShellMode.Yard, "the first tile has number 3, and no workspace has 1");

        await _h.Shell.JumpToAsync(3);
        _h.Shell.Mode.ShouldBe(ShellMode.Cab);
        _h.Shell.Cab.ActiveTile!.Id.ShouldBe(_h.App.Id);
    }

    [Fact]
    public async Task Settings_mode_hides_vscode_windows()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        _h.Shell.Cab.LastHostRect = ScreenRect.FromSize(0, 0, 100, 100);
        await _h.Shell.EnterCabAsync(_h.App.Id);
        _h.Docker.ClearReceivedCalls(); // discovery cloaks once; only the Settings cloak counts below

        _h.Shell.OpenSettings();

        _h.Shell.Mode.ShouldBe(ShellMode.Settings);
        _h.Docker.Received(1).Cloak(500);
    }

    [Fact]
    public async Task Workspaces_marked_AutoStart_are_launched_and_kept_hidden_at_startup()
    {
        _h.App.AutoStart = true;
        _h.VsCodeWindowAppears();

        await _h.Shell.InitializeAsync(CancellationToken.None);
        for (var i = 0; i < 100 && _h.Host.Get(_h.App.Id)?.State != HostState.Running; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        _h.Launcher.Received(1).Launch(_h.App);
        _h.Host.Get(_h.App.Id)!.State.ShouldBe(HostState.Running);
        _h.Shell.Mode.ShouldBe(ShellMode.Yard);
        _h.Docker.Received().Cloak(500);
        _h.Docker.DidNotReceive().Uncloak(500);
    }

    [Fact]
    public async Task Minimising_the_shell_in_cab_mode_hides_vscode_and_restoring_redocks_it()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        var rect = ScreenRect.FromSize(0, 28, 1600, 900);
        _h.Shell.Cab.LastHostRect = rect;
        await _h.Shell.EnterCabAsync(_h.App.Id);
        _h.Docker.ClearReceivedCalls();

        _h.Shell.SetShellMinimized(true);
        _h.Docker.Received(1).Cloak(500);

        var offScreen = ScreenRect.FromSize(-32000, -32000, 1600, 900);
        _h.Shell.UpdateCabRect(offScreen);
        _h.Docker.DidNotReceive().MoveTo(500, offScreen);

        _h.Shell.SetShellMinimized(false);
        _h.Docker.Received(1).Uncloak(500);
        _h.Docker.Received(1).MoveTo(500, rect);
        _h.Shell.Mode.ShouldBe(ShellMode.Cab);
    }

    [Fact]
    public async Task Opening_a_tile_while_its_autostart_discovery_is_still_running_docks_it_when_discovery_finishes()
    {
        _h.App.AutoStart = true;
        _h.VsCodeWindowAppears();
        var rect = ScreenRect.FromSize(0, 28, 1600, 900);
        _h.Shell.Cab.LastHostRect = rect;

        await _h.Shell.InitializeAsync(CancellationToken.None);
        await _h.Shell.EnterCabAsync(_h.App.Id);

        _h.Shell.StatusMessage.ShouldBeNull("an in-flight start is not a failure");
        _h.Shell.Mode.ShouldBe(ShellMode.Cab);
        _h.Launcher.Received(1).Launch(_h.App);
        _h.Docker.Received().MoveTo(500, rect);
    }

    [Fact]
    public async Task Resizing_the_cab_repositions_vscode_without_raising_it_and_activation_raises_it()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        var rect = ScreenRect.FromSize(0, 28, 1600, 900);
        _h.Shell.Cab.LastHostRect = rect;
        await _h.Shell.EnterCabAsync(_h.App.Id);
        _h.Docker.ClearReceivedCalls();
        var resized = ScreenRect.FromSize(0, 28, 1200, 700);

        _h.Shell.UpdateCabRect(resized);

        _h.Docker.Received(1).MoveTo(500, resized);
        _h.Docker.DidNotReceive().BringToFront(Arg.Any<nint>());

        _h.Shell.RaiseHostedWindow();

        _h.Docker.Received(1).BringToFront(500);
    }

    [Fact]
    public async Task A_raise_without_focus_puts_vscode_on_top_and_leaves_the_foreground_to_the_shell()
    {
        // Taking the foreground while the click that activated the shell was still down took the mouse from the shell's
        // button, which then never clicked: the ← Yard button did nothing while VS Code had the focus.
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        _h.Shell.Cab.LastHostRect = ScreenRect.FromSize(0, 28, 1600, 900);
        await _h.Shell.EnterCabAsync(_h.App.Id);
        _h.Docker.ClearReceivedCalls();

        _h.Shell.RaiseHostedWindow(focus: false);

        _h.Docker.Received(1).PlaceOnTop(500);
        _h.Docker.DidNotReceive().BringToFront(Arg.Any<nint>());
    }

    [Fact]
    public async Task Switching_inside_the_cab_to_a_workspace_that_still_has_to_start_hides_the_one_shown_so_far()
    {
        var shop = AddShopWithItsWindowOpen();
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Shell.Cab.LastHostRect = ScreenRect.FromSize(0, 28, 1600, 900);
        await _h.Shell.EnterCabAsync(shop.Id);
        _h.Host.Get(shop.Id)!.Visible.ShouldBeTrue();
        _h.Docker.ClearReceivedCalls(); // discovery cloaks once; only the switch counts below

        var openApp = _h.Shell.EnterCabAsync(_h.App.Id);

        _h.Docker.Received(1).Cloak(ShopHwnd);
        await openApp;
        _h.Host.Get(_h.App.Id)!.State.ShouldBe(HostState.Stopped);
        _h.Host.Get(shop.Id)!.Visible.ShouldBeFalse("the strip names App, so Shop's VS Code must not stay in the Cab under that name");
    }

    [Fact]
    public async Task An_open_that_fails_after_the_user_moved_on_leaves_the_status_of_the_workspace_now_shown_alone()
    {
        var shop = AddShopWithItsWindowOpen();
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Shell.Cab.LastHostRect = ScreenRect.FromSize(0, 28, 1600, 900);

        var openApp = _h.Shell.EnterCabAsync(_h.App.Id);
        await _h.Shell.EnterCabAsync(shop.Id);
        await openApp;

        _h.Host.Get(_h.App.Id)!.State.ShouldBe(HostState.Stopped);
        _h.Shell.ActiveWorkspaceId.ShouldBe(shop.Id);
        _h.Shell.StatusMessage.ShouldBeNull("App's error does not belong in Shop's strip");
    }

    [Fact]
    public async Task Unregistering_the_active_workspace_clears_the_cab_and_returns_to_the_yard()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        _h.Shell.Cab.LastHostRect = ScreenRect.FromSize(0, 28, 1600, 900);
        await _h.Shell.EnterCabAsync(_h.App.Id);

        _h.Bus.Publish(new WorkspaceUnregistered(_h.App.Id));

        _h.Shell.Mode.ShouldBe(ShellMode.Yard);
        _h.Shell.ActiveWorkspaceId.ShouldBeNull("Ctrl+Alt+Y would otherwise bring the shell forward and do nothing");
        _h.Shell.Cab.ActiveTile.ShouldBeNull();
        _h.Shell.Cab.Pips.ShouldBeEmpty();
    }

    [Fact]
    public async Task Closing_the_vscode_window_shown_in_the_cab_returns_to_the_yard()
    {
        // Closed by its X button, VS Code left an empty Cab behind, and the user had to find the way back themselves.
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        _h.Shell.Cab.LastHostRect = ScreenRect.FromSize(0, 28, 1600, 900);
        await _h.Shell.EnterCabAsync(_h.App.Id);

        _h.Host.WindowDestroyed(500);

        _h.Shell.Mode.ShouldBe(ShellMode.Yard);
    }

    [Fact]
    public async Task A_vscode_window_that_closes_while_the_cab_shows_another_workspace_leaves_the_cab_alone()
    {
        var shop = new Workspace { Name = "Shop", RootPath = @"c:\repo\shop", TrackId = _h.General.Id };
        _h.Workspaces.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([_h.App, shop]));
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        _h.Shell.Cab.LastHostRect = ScreenRect.FromSize(0, 28, 1600, 900);
        await _h.Shell.EnterCabAsync(_h.App.Id);
        _h.Windows.TopLevelWindows().Returns([new WindowInfo(ShopHwnd, 31, "Chrome_WidgetWin_1", "Program.cs - Shop - Visual Studio Code")]);
        await _h.Shell.EnterCabAsync(shop.Id);

        _h.Host.WindowDestroyed(500);

        _h.Shell.Mode.ShouldBe(ShellMode.Cab);
        _h.Shell.ActiveWorkspaceId.ShouldBe(shop.Id);
    }

    [Fact]
    public async Task Unregistering_the_workspace_being_opened_makes_that_open_stale()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Windows.TopLevelWindows().Returns([]);
        using var launchMayEnd = new ManualResetEventSlim();
        _h.Launcher.Launch(Arg.Any<Workspace>()).Returns(_ =>
        {
            launchMayEnd.Wait(TimeSpan.FromSeconds(10));
            return new LaunchResult(false, null, "code not found");
        });

        var open = _h.Shell.EnterCabAsync(_h.App.Id);
        _h.Bus.Publish(new WorkspaceUnregistered(_h.App.Id)); // the shell is back on the Yard
        launchMayEnd.Set();
        await open;

        _h.Shell.Mode.ShouldBe(ShellMode.Yard);
        _h.Shell.StatusMessage.ShouldBeNull("the error belongs to a workspace that is gone");
    }

    [Fact]
    public async Task Unregistering_a_workspace_that_is_not_active_removes_its_pip()
    {
        var shop = AddShopWithItsWindowOpen();
        var cli = new Workspace { Name = "Cli", RootPath = @"c:\repo\cli", TrackId = _h.General.Id };
        _h.Workspaces.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([_h.App, shop, cli]));
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Shell.Cab.LastHostRect = ScreenRect.FromSize(0, 28, 1600, 900);
        await _h.Shell.EnterCabAsync(shop.Id);
        _h.Shell.Cab.Pips.Select(p => p.Name).ShouldBe(["App", "Cli"]);

        _h.Bus.Publish(new WorkspaceUnregistered(cli.Id));

        _h.Shell.Cab.Pips.Select(p => p.Name).ShouldBe(["App"], "a pip for a gone workspace switches to nothing");
        _h.Shell.ActiveWorkspaceId.ShouldBe(shop.Id);
    }

    [Fact]
    public async Task A_failed_open_that_ends_as_the_user_retries_does_not_write_its_error_over_the_retry()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Windows.TopLevelWindows().Returns([]);
        _h.Launcher.Launch(Arg.Any<Workspace>()).Returns(new LaunchResult(false, null, "code not found"));
        var ui = new QueuedSynchronizationContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            var first = _h.Shell.EnterCabAsync(_h.App.Id);
            SpinWait.SpinUntil(() => ui.Pending > 0, TimeSpan.FromSeconds(5)).ShouldBeTrue("the open has failed and its report waits for the UI thread");
            var retry = _h.Shell.EnterCabAsync(_h.App.Id); // the click lands first: a new open is running
            _h.Shell.StatusMessage.ShouldBeNull();

            ui.RunOne(TimeSpan.FromSeconds(5)); // now the first open's continuation reports "code not found"

            _h.Shell.StatusMessage.ShouldBeNull("the retry is running; the error of the open before it is stale");
            ui.RunUntil(() => first.IsCompleted && retry.IsCompleted, TimeSpan.FromSeconds(5));
            await first;
            await retry;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public async Task The_yard_learns_from_the_installer_whether_the_hooks_are_installed()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Shell.Yard.HooksInstalled.ShouldBeFalse();
        _h.Shell.Settings.RelayExecutable = Path.Combine(AppContext.BaseDirectory, "relay", "csx-hook.exe");

        await _h.Shell.Settings.InstallHooksCommand.ExecuteAsync(null);

        _h.Shell.Settings.HookState.ShouldBe(HookInstallState.Installed, _h.Shell.Settings.LastMessage);
        _h.Shell.Yard.HooksInstalled.ShouldBeTrue("the banner must go when Install hooks is clicked, not when a chat happens to send a hook");
    }

    [Fact]
    public async Task An_open_that_finishes_while_the_shell_is_minimised_shows_vscode_only_once_the_shell_is_restored()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        var rect = ScreenRect.FromSize(0, 28, 1600, 900);
        _h.Shell.Cab.LastHostRect = rect;
        _h.VsCodeWindowAppears();
        using var launchMayEnd = new ManualResetEventSlim();
        _h.Launcher.Launch(Arg.Any<Workspace>()).Returns(_ =>
        {
            launchMayEnd.Wait(TimeSpan.FromSeconds(10));
            return new LaunchResult(true, 1, null);
        });

        var open = _h.Shell.EnterCabAsync(_h.App.Id);
        _h.Shell.SetShellMinimized(true);
        launchMayEnd.Set();
        await open;

        _h.Host.Get(_h.App.Id)!.State.ShouldBe(HostState.Running);
        _h.Docker.DidNotReceive().Uncloak(500);
        _h.Docker.DidNotReceive().BringToFront(500);

        _h.Shell.SetShellMinimized(false);

        _h.Docker.Received(1).Uncloak(500);
        _h.Docker.Received(1).MoveTo(500, rect);
    }

    [Fact]
    public async Task The_panel_s_chips_show_what_new_chats_run_with_as_Settings_changes()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        (_h.Shell.Raven.ChatModelChip, _h.Shell.Raven.ChatEffortChip).ShouldBe(("Default model", "Default effort"));

        _h.Shell.Settings.RavenChatModel = "Fable";
        _h.Shell.Settings.RavenChatEffort = "high";
        (_h.Shell.Raven.ChatModelChip, _h.Shell.Raven.ChatEffortChip).ShouldBe(("Fable 5.1", "high effort"));

        _h.Shell.Settings.RavenModelAliases = "Fable = claude-fable-6-0";
        _h.Shell.Raven.ChatModelChip.ShouldBe("Fable 6.0");

        _h.Shell.Settings.RavenChatModel = "claude-custom-1";
        _h.Shell.Raven.ChatModelChip.ShouldBe("Custom 1");

        // A name the table no longer has starts Claude Code's default, and the chip says so.
        _h.Shell.Settings.RavenChatModel = "Fable";
        _h.Shell.Settings.RavenModelAliases = "Opus = claude-opus-5-5";
        _h.Shell.Raven.ChatModelChip.ShouldBe("Default model");
    }

    [Fact]
    public async Task A_line_of_Ravens_digest_card_shows_the_Yard_with_its_tile_lit_for_a_moment()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        _h.Shell.Cab.LastHostRect = ScreenRect.FromSize(0, 0, 100, 100);
        await _h.Shell.EnterCabAsync(_h.App.Id);

        _h.Shell.ShowTile(_h.App.Id);

        _h.Shell.Mode.ShouldBe(ShellMode.Yard);
        _h.Shell.Yard.FindTile(_h.App.Id)!.IsSpotlit.ShouldBeTrue();
    }

    [Fact]
    public async Task Speaking_chat_news_follows_the_setting()
    {
        _h.Settings.GetAsync<bool?>(SettingKeys.RavenSpeakNews, Arg.Any<CancellationToken>()).Returns(Task.FromResult<bool?>(false));

        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Shell.Raven.SpeakNews.ShouldBeFalse();

        _h.Shell.Settings.RavenSpeakNews = true;

        _h.Shell.Raven.SpeakNews.ShouldBeTrue();
        await _h.Shell.Settings.FlushSavesAsync(CancellationToken.None);
        await _h.Settings.Received(1).SetAsync(SettingKeys.RavenSpeakNews, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_mic_mode_is_restored_and_stored()
    {
        _h.Settings.GetAsync<string?>(SettingKeys.RavenMicMode, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("OpenMic"));

        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Shell.Raven.MicMode.ShouldBe(MicMode.OpenMic);

        _h.Shell.Raven.ChooseMicModeCommand.Execute(MicMode.PushToTalk); // the user's switch

        await _h.Shell.Settings.FlushSavesAsync(CancellationToken.None);
        await _h.Settings.Received(1).SetAsync(SettingKeys.RavenMicMode, "PushToTalk", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_fall_back_to_push_to_talk_after_a_failed_download_leaves_the_stored_Open_mic_alone()
    {
        _h.Settings.GetAsync<string?>(SettingKeys.RavenMicMode, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("OpenMic"));
        _h.OpenMic.ModelsPresent = false;
        _h.OpenMic.DownloadFails = new System.Net.Http.HttpRequestException("offline");

        await _h.Shell.InitializeAsync(CancellationToken.None);
        await _h.Shell.Raven.PendingOpenMic.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        _h.Shell.Raven.MicMode.ShouldBe(MicMode.PushToTalk);
        _h.Shell.Raven.PreferredMicMode.ShouldBe(MicMode.OpenMic);
        await _h.Shell.Settings.FlushSavesAsync(CancellationToken.None);
        await _h.Settings.DidNotReceive().SetAsync(SettingKeys.RavenMicMode, "PushToTalk", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task After_a_fall_back_the_user_choosing_push_to_talk_is_stored()
    {
        _h.Settings.GetAsync<string?>(SettingKeys.RavenMicMode, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("OpenMic"));
        _h.OpenMic.ModelsPresent = false;
        _h.OpenMic.DownloadFails = new System.Net.Http.HttpRequestException("offline");
        await _h.Shell.InitializeAsync(CancellationToken.None);
        await _h.Shell.Raven.PendingOpenMic.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _h.Shell.Raven.MicMode.ShouldBe(MicMode.PushToTalk);

        _h.Shell.Raven.ChooseMicModeCommand.Execute(MicMode.PushToTalk); // the half already checked

        await _h.Shell.Settings.FlushSavesAsync(CancellationToken.None);
        await _h.Settings.Received(1).SetAsync(SettingKeys.RavenMicMode, "PushToTalk", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_stored_mic_mode_reads_as_push_to_talk()
    {
        _h.Settings.GetAsync<string?>(SettingKeys.RavenMicMode, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("Shout"));

        await _h.Shell.InitializeAsync(CancellationToken.None);

        _h.Shell.Raven.MicMode.ShouldBe(MicMode.PushToTalk);
    }

    [Fact]
    public async Task The_traffic_watcher_follows_the_voice_settings()
    {
        _h.Settings.GetAsync<int?>(SettingKeys.RavenCooldownSeconds, Arg.Any<CancellationToken>()).Returns(Task.FromResult<int?>(20));
        _h.Settings.GetAsync<bool?>(SettingKeys.RavenChatSound, Arg.Any<CancellationToken>()).Returns(Task.FromResult<bool?>(false));

        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Shell.Raven.Traffic.Cooldown.ShouldBe(TimeSpan.FromSeconds(20));
        _h.Shell.Raven.Traffic.SoundOn.ShouldBeFalse();
        _h.Shell.Raven.Traffic.OwnNewsWaits.ShouldBeFalse();

        _h.Shell.Settings.RavenCooldownSeconds = 30;
        _h.Shell.Settings.RavenChatSound = true;
        _h.Shell.Settings.RavenOwnNewsWaits = true;
        _h.Shell.Raven.CatchUp.ShouldBeFalse("off by default");
        _h.Shell.Settings.RavenCatchUp = true;

        _h.Shell.Raven.CatchUp.ShouldBeTrue();
        _h.Shell.Raven.Traffic.Cooldown.ShouldBe(TimeSpan.FromSeconds(30));
        _h.Shell.Raven.Traffic.SoundOn.ShouldBeTrue();
        _h.Shell.Raven.Traffic.OwnNewsWaits.ShouldBeTrue();
    }

    [Fact]
    public async Task Barge_in_follows_the_setting()
    {
        _h.Settings.GetAsync<bool?>(SettingKeys.RavenBargeIn, Arg.Any<CancellationToken>()).Returns(Task.FromResult<bool?>(false));

        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Shell.Raven.BargeIn.ShouldBeFalse();

        _h.Shell.Settings.RavenBargeIn = true;

        _h.Shell.Raven.BargeIn.ShouldBeTrue();
        await _h.Shell.Settings.FlushSavesAsync(CancellationToken.None);
        await _h.Settings.Received(1).SetAsync(SettingKeys.RavenBargeIn, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Settings_Voice_opens_by_itself_once_with_a_welcome_when_the_Raven_panel_is_first_used_with_no_engine()
    {
        _h.Settings.GetAsync<bool?>(SettingKeys.RavenPanelOpen, Arg.Any<CancellationToken>()).Returns(Task.FromResult<bool?>(false));
        await _h.Shell.InitializeAsync(CancellationToken.None);

        _h.Shell.OfferVoiceSetup(); // the window shows, the panel folded
        _h.Shell.Mode.ShouldBe(ShellMode.Yard);

        _h.Shell.Raven.IsOpen = true;
        (_h.Shell.Mode, _h.Shell.Settings.Page, _h.Shell.Settings.VoicePage.ShowWelcome).ShouldBe((ShellMode.Settings, SettingsPage.Voice, true));
        _h.Shell.Settings.RavenVoiceSetupShown.ShouldBeTrue();

        _h.Shell.CloseSettings();
        _h.Shell.Settings.VoicePage.ShowWelcome.ShouldBeFalse();
        _h.Shell.Raven.IsOpen = false;
        _h.Shell.Raven.IsOpen = true;
        _h.Shell.OfferVoiceSetup();
        _h.Shell.Mode.ShouldBe(ShellMode.Yard, "skipped once, it opens from Settings only");
    }

    [Fact]
    public async Task With_an_engine_picked_Settings_does_not_open_by_itself()
    {
        _h.Qwen.IsInstalled = true;
        await _h.Shell.InitializeAsync(CancellationToken.None);

        _h.Shell.OfferVoiceSetup();

        _h.Shell.Mode.ShouldBe(ShellMode.Yard);
    }

    [Fact]
    public async Task Leaving_Settings_for_the_Cab_closes_it_as_Back_to_Yard_does()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Shell.OpenSettingsAt(SettingsPage.Voice);
        _h.Shell.Settings.VoicePage.ShowWelcome = true;

        _h.VsCodeWindowAppears();
        _h.Shell.Cab.LastHostRect = ScreenRect.FromSize(0, 28, 1600, 900);
        await _h.Shell.EnterCabAsync(_h.App.Id); // Raven opens a workspace, or a jump hotkey

        _h.Shell.Mode.ShouldBe(ShellMode.Cab);
        _h.Shell.Settings.VoicePage.ShowWelcome.ShouldBeFalse("the first-run line has had its turn");
    }

    [Fact]
    public async Task A_dot_on_the_bottom_bar_or_the_chips_open_their_page_of_Settings()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);

        _h.Shell.OpenSettingsAtCommand.Execute(SettingsPage.Listening);
        (_h.Shell.Mode, _h.Shell.Settings.Page).ShouldBe((ShellMode.Settings, SettingsPage.Listening));

        _h.Shell.OpenSettingsAtCommand.Execute(SettingsPage.Brain);
        _h.Shell.Settings.Page.ShouldBe(SettingsPage.Brain);
        _h.Shell.Settings.VoicePage.ShowWelcome.ShouldBeFalse("only the first run says welcome");
    }
}
