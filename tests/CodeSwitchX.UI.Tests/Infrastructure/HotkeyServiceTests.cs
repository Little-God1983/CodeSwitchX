using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.UI.Shell;
using CodeSwitchX.Voice.Audio;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Infrastructure;

public class HotkeyServiceTests
{
    [Fact]
    public void Digit_hotkeys_never_use_bare_ctrl_alt_because_that_is_what_altgr_sends()
    {
        var digits = HotkeyService.Bindings.Where(b => b.VirtualKey is >= 0x31 and <= 0x39).ToList();

        digits.Count.ShouldBe(9);
        foreach (var binding in digits)
        {
            var modifiers = binding.Modifiers & ~HotkeyModifiers.NoRepeat;
            modifiers.HasFlag(HotkeyModifiers.Control | HotkeyModifiers.Alt).ShouldBeTrue(binding.Label);
            modifiers.ShouldNotBe(HotkeyModifiers.Control | HotkeyModifiers.Alt, binding.Label + " would swallow AltGr+digit on German keyboards");
        }
    }

    // AltGr reaches RegisterHotKey as Ctrl+Alt, so a Ctrl+Alt hotkey takes AltGr+that key away from every application.
    // These keys type a character with AltGr on common European layouts (German ² ³ { [ ] } \ @ € µ ~ |, French, Spanish,
    // Italian, Polish ą ć ę ł ń ó ś ź ż): a Ctrl+Alt hotkey must not use them. Space is allowed: AltGr+Space types
    // nothing on those layouts.
    private static readonly uint[] AltGrCharacterKeys =
    [
        0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, // digits
        0x41, 0x43, 0x45, 0x4C, 0x4D, 0x4E, 0x4F, 0x51, 0x53, 0x58, 0x5A, // A C E L M N O Q S X Z
        0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE, 0xE2, // OEM punctuation, and < > | on the 102nd key
    ];

    [Fact]
    public void Ctrl_alt_hotkeys_only_use_keys_that_type_no_altgr_character()
    {
        var ctrlAlt = HotkeyService.Bindings
            .Where(b => (b.Modifiers & ~HotkeyModifiers.NoRepeat) == (HotkeyModifiers.Control | HotkeyModifiers.Alt))
            .ToList();

        ctrlAlt.Select(b => b.Keys).ShouldBe(["Ctrl+Alt+Y", "Ctrl+Alt+Space", "Ctrl+Alt+J"], ignoreOrder: true);
        foreach (var binding in ctrlAlt)
        {
            AltGrCharacterKeys.ShouldNotContain(binding.VirtualKey, binding.Keys + " would swallow an AltGr character");
        }
    }

    [Fact]
    public void Toggle_hotkey_stays_ctrl_alt_y()
    {
        var toggle = HotkeyService.Bindings.Single(b => b.VirtualKey == 0x59);

        (toggle.Modifiers & ~HotkeyModifiers.NoRepeat).ShouldBe(HotkeyModifiers.Control | HotkeyModifiers.Alt);
        toggle.Label.ShouldBe("Ctrl+Alt+Y");
    }

    // Ctrl+Shift+Space is VS Code's Trigger Parameter Hints, in the very window CodeSwitchX docks.
    [Fact]
    public void Push_to_talk_is_ctrl_alt_space_without_autorepeat()
    {
        var talk = HotkeyService.Bindings.Single(b => b.Id == 21);

        talk.Modifiers.ShouldBe(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.NoRepeat,
            "an autorepeat must not press the mic again while the keys are held");
        talk.VirtualKey.ShouldBe(0x20u);
        talk.Label.ShouldBe("Push to talk");
        talk.Keys.ShouldBe("Ctrl+Alt+Space");
    }

    // The chord is defined once: changing it changes the registration and every text that names it.
    [Fact]
    public void The_push_to_talk_chord_is_defined_once_and_every_hint_names_it_from_there()
    {
        var talk = HotkeyService.PushToTalk;

        HotkeyService.Bindings.Single(b => b.Id == 21).ShouldBeSameAs(talk);
        RavenPanelViewModel.IdleCaption.ShouldBe($"Hold {talk.Keys} or the mic button to talk.");
        RavenPanelViewModel.MicToolTip.ShouldBe($"Hold to talk, or tap to keep listening until the next tap ({talk.Keys})");
        HotkeyService.UnseenReleaseNote.ShouldBe(
            $"Push to talk can't see the key being released while an admin window is in front. Press {talk.Keys} again to stop.");
    }

    [Fact]
    public void Ctrl_alt_j_collapses_or_expands_raven()
    {
        var toggle = HotkeyService.Bindings.Single(b => b.Id == 22);

        (toggle.Modifiers & ~HotkeyModifiers.NoRepeat).ShouldBe(HotkeyModifiers.Control | HotkeyModifiers.Alt);
        toggle.Modifiers.HasFlag(HotkeyModifiers.NoRepeat).ShouldBeTrue("a held chord must not fold and unfold the panel over and over");
        toggle.VirtualKey.ShouldBe(0x4Au);
        toggle.Label.ShouldBe("Collapse or expand Raven");
        toggle.Keys.ShouldBe("Ctrl+Alt+J");
    }

    // Each Raven hotkey has its own button to fall back on: the mic for push-to-talk, the arrow for folding the panel.
    [Fact]
    public void A_taken_raven_hotkey_is_explained_with_the_button_that_does_its_job()
    {
        var talk = HotkeyService.Bindings.Single(b => b.Id == 21);
        var fold = HotkeyService.Bindings.Single(b => b.Id == 22);

        HotkeyService.RavenFailureNote(talk).ShouldBe("Push to talk (Ctrl+Alt+Space) is taken by another app. Use the mic button instead.");
        HotkeyService.RavenFailureNote(fold).ShouldBe("Collapse or expand Raven (Ctrl+Alt+J) is taken by another app. Use the arrow button instead.");
        HotkeyService.RavenFailureNote(HotkeyService.Bindings.Single(b => b.Label == "Ctrl+Alt+Y")).ShouldBeNull("not Raven's to explain");
    }

    [Fact]
    public void Every_binding_names_its_keys()
    {
        HotkeyService.Bindings.Single(b => b.Label == "Ctrl+Alt+Y").Keys.ShouldBe("Ctrl+Alt+Y");
        HotkeyService.Bindings.Single(b => b.Label == "Ctrl+Shift+Alt+4").Keys.ShouldBe("Ctrl+Shift+Alt+4");
    }

    [Fact]
    public async Task Ctrl_alt_j_folds_the_panel_and_leaves_a_minimised_shell_where_it_is()
    {
        var harness = new ShellTestHarness();
        await harness.Shell.InitializeAsync(CancellationToken.None);
        await harness.Shell.Raven.PendingRefresh; // the microphones are listed off the UI thread

        await OnMinimisedShellAsync(harness, "Collapse or expand Raven", window =>
        {
            harness.Shell.Raven.IsOpen.ShouldBeFalse();
            window.WindowState.ShouldBe(WindowState.Minimized, "talking to Raven must not take the foreground from VS Code");
        });
    }

    [Fact]
    public async Task Push_to_talk_starts_listening_and_leaves_a_minimised_shell_where_it_is()
    {
        var harness = new ShellTestHarness();
        var headset = new MicrophoneDevice("id-headset", "Headset");
        harness.Microphones.List().Returns([headset]);
        harness.Microphones.Default().Returns(headset);
        await harness.Shell.InitializeAsync(CancellationToken.None);
        await harness.Shell.Raven.PendingRefresh; // the microphones are listed off the UI thread

        await OnMinimisedShellAsync(harness, "Push to talk", window =>
        {
            harness.Shell.Raven.State.ShouldBe(RavenState.Listening);
            PumpUntil(() => harness.Shell.Raven.PendingStart.IsCompleted); // the recorder starts off the UI thread
            harness.Recorder.Received(1).Start(headset.Id);
            window.WindowState.ShouldBe(WindowState.Minimized, "talking to Raven must not take the foreground from VS Code");
        });
    }

    // An elevated window in front hides the keyboard from GetAsyncKeyState: the held Space reads as up. Polled, that
    // hold would latch and record on after the user let go; so when the key reads up and an elevated window is in
    // front, the press is a tap on purpose and the next one stops.
    [Fact]
    public async Task Push_to_talk_over_an_elevated_window_latches_says_so_once_and_the_next_press_stops()
    {
        var harness = RecordingHarness();
        await harness.Shell.InitializeAsync(CancellationToken.None);
        await harness.Shell.Raven.PendingRefresh; // the microphones are listed off the UI thread
        var raven = harness.Shell.Raven;

        await WithHotkeysAsync(harness, _ => false, elevated: true, TimeSpan.Zero, press =>
        {
            press();
            raven.State.ShouldBe(RavenState.Listening, "a tap latches the mic on");
            PumpFor(TimeSpan.FromMilliseconds(150));
            raven.State.ShouldBe(RavenState.Listening, "no release poll ends it");

            press();
            raven.State.ShouldNotBe(RavenState.Listening, "the next press stops");
            PumpUntil(() => raven.State == RavenState.Idle);

            press();
            raven.State.ShouldBe(RavenState.Listening);
        });

        raven.Log.Count(l => l.Text == HotkeyService.UnseenReleaseNote).ShouldBe(1, "once per session");
        raven.Log.Single(l => l.Text == HotkeyService.UnseenReleaseNote).Kind.ShouldBe(RavenLogKind.Note);
    }

    [Fact]
    public async Task Push_to_talk_whose_key_reads_as_held_is_stopped_by_the_release_poll()
    {
        var harness = RecordingHarness();
        await harness.Shell.InitializeAsync(CancellationToken.None);
        await harness.Shell.Raven.PendingRefresh; // the microphones are listed off the UI thread
        var raven = harness.Shell.Raven;
        var held = true;

        await WithHotkeysAsync(harness, _ => held, elevated: false, TimeSpan.Zero, press =>
        {
            press();
            raven.State.ShouldBe(RavenState.Listening);
            PumpFor(TimeSpan.FromMilliseconds(150));
            raven.State.ShouldBe(RavenState.Listening, "the keys are still held");

            harness.Time.Advance(TimeSpan.FromSeconds(1));
            held = false;
            PumpUntil(() => raven.State == RavenState.Idle);
        });

        raven.Log.ShouldNotContain(l => l.Text == HotkeyService.UnseenReleaseNote);
        harness.Recorder.Received(1).Stop();
    }

    // A key that reads as down is the proof the keyboard is visible, whatever the foreground's elevation reads as: the
    // release poll works there, and an elevation check that guessed wrong would latch a hold.
    [Fact]
    public async Task Push_to_talk_whose_key_reads_as_held_uses_the_release_poll_even_over_a_window_that_reads_as_elevated()
    {
        var harness = RecordingHarness();
        await harness.Shell.InitializeAsync(CancellationToken.None);
        await harness.Shell.Raven.PendingRefresh; // the microphones are listed off the UI thread
        var raven = harness.Shell.Raven;
        var held = true;

        await WithHotkeysAsync(harness, _ => held, elevated: true, TimeSpan.Zero, press =>
        {
            press();
            PumpFor(TimeSpan.FromMilliseconds(150));
            raven.State.ShouldBe(RavenState.Listening, "the keys are still held");

            harness.Time.Advance(TimeSpan.FromSeconds(1));
            held = false;
            PumpUntil(() => raven.State == RavenState.Idle);
        });

        raven.Log.ShouldNotContain(l => l.Text == HotkeyService.UnseenReleaseNote);
        harness.Recorder.Received(1).Stop();
    }

    // The chord is held while Space, Ctrl and Alt are all down: letting go of any of them ends it. Shift is not part of
    // it any more and is not watched.
    [Theory]
    [InlineData(0x11u)] // Ctrl
    [InlineData(0x12u)] // Alt
    [InlineData(0x20u)] // Space
    public async Task Letting_go_of_any_key_of_the_chord_ends_a_held_push_to_talk(uint letGo)
    {
        var harness = RecordingHarness();
        await harness.Shell.InitializeAsync(CancellationToken.None);
        await harness.Shell.Raven.PendingRefresh; // the microphones are listed off the UI thread
        var raven = harness.Shell.Raven;
        var released = false;

        await WithHotkeysAsync(harness, vk => !(released && vk == letGo) && vk != 0x10, elevated: false, TimeSpan.Zero, press =>
        {
            press();
            PumpFor(TimeSpan.FromMilliseconds(150));
            raven.State.ShouldBe(RavenState.Listening, "Ctrl, Alt and Space are held; Shift reading up does not matter");

            harness.Time.Advance(TimeSpan.FromSeconds(1));
            released = true;
            PumpUntil(() => raven.State == RavenState.Idle);
        });

        harness.Recorder.Received(1).Stop();
    }

    // The UI thread was busy while the user held the chord for a second and let go: WM_HOTKEY is handled after the
    // release. Timed from the message, the press is a hold, and the first poll that finds the key up stops it.
    [Fact]
    public async Task A_hold_the_ui_thread_handled_late_stops_at_the_first_poll_instead_of_latching()
    {
        var harness = RecordingHarness();
        await harness.Shell.InitializeAsync(CancellationToken.None);
        await harness.Shell.Raven.PendingRefresh; // the microphones are listed off the UI thread
        var raven = harness.Shell.Raven;

        await WithHotkeysAsync(harness, _ => false, elevated: false, TimeSpan.FromSeconds(1), press =>
        {
            press();
            PumpUntil(() => raven.State == RavenState.Idle);
        });

        harness.Recorder.Received(1).Stop();
        raven.Log.ShouldNotContain(l => l.Text == HotkeyService.UnseenReleaseNote, "no admin window was in front");
    }

    // A quick tap, released before the UI thread handled it: the key already reads up, which is a tap, not an admin
    // window.
    [Fact]
    public async Task A_quick_tap_whose_key_is_already_up_latches_without_the_admin_window_note()
    {
        var harness = RecordingHarness();
        await harness.Shell.InitializeAsync(CancellationToken.None);
        await harness.Shell.Raven.PendingRefresh; // the microphones are listed off the UI thread
        var raven = harness.Shell.Raven;

        await WithHotkeysAsync(harness, _ => false, elevated: false, TimeSpan.FromMilliseconds(50), press =>
        {
            press();
            PumpFor(TimeSpan.FromMilliseconds(150));
            raven.State.ShouldBe(RavenState.Listening, "a tap latches the mic on");
        });

        raven.Log.ShouldNotContain(l => l.Text == HotkeyService.UnseenReleaseNote);
        harness.Recorder.DidNotReceive().Stop();
    }

    private static ShellTestHarness RecordingHarness()
    {
        var harness = new ShellTestHarness();
        var headset = new MicrophoneDevice("id-headset", "Headset");
        harness.Microphones.List().Returns([headset]);
        harness.Microphones.Default().Returns(headset);
        harness.Recorder.Stop().Returns(new RecordedClip([], TimeSpan.Zero));
        return harness;
    }

    /// <summary>A window wired to the shell with a hotkey service reading the keys through <paramref name="isKeyDown"/>,
    /// seeing the foreground as <paramref name="elevated"/> or not and each WM_HOTKEY as sent <paramref name="messageAge"/>
    /// ago; the body gets a press of push-to-talk and runs on the window's thread.</summary>
    private static Task WithHotkeysAsync(ShellTestHarness harness, Func<uint, bool> isKeyDown, bool elevated, TimeSpan messageAge,
        Action<Action> body)
    {
        var binding = HotkeyService.PushToTalk;
        return StaThread.RunAsync(() =>
        {
            var window = HiddenWindow();
            var hwnd = new WindowInteropHelper(window).Handle;
            var hotkeys = new HotkeyService(NullLogger<HotkeyService>.Instance, isKeyDown, () => elevated, () => messageAge);
            hotkeys.Attach(hwnd, harness.Shell);
            try
            {
                body(() => StaThread.SendMessage(hwnd, HotkeyInterop.WmHotkey, binding.Id, 0));
            }
            finally
            {
                hotkeys.Detach();
                window.Close();
            }
        });
    }

    private static void PumpFor(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        PumpUntil(() => DateTime.UtcNow >= until);
    }

    /// <summary>Runs the window thread's dispatcher (timers, awaited continuations) until the condition holds.</summary>
    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the condition never held");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(5), DispatcherPriority.Background, (sender, _) =>
            {
                ((DispatcherTimer)sender!).Stop();
                frame.Continue = false;
            }, Dispatcher.CurrentDispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    [Fact]
    public async Task A_hotkey_another_window_holds_is_reported_as_failed()
    {
        var harness = new ShellTestHarness();

        await StaThread.RunAsync(() =>
        {
            var first = HiddenWindow();
            var second = HiddenWindow();
            var holder = new HotkeyService(NullLogger<HotkeyService>.Instance);
            var late = new HotkeyService(NullLogger<HotkeyService>.Instance);
            try
            {
                holder.Attach(new WindowInteropHelper(first).Handle, harness.Shell);
                late.Attach(new WindowInteropHelper(second).Handle, harness.Shell);

                late.FailedBindings.Select(b => b.Id).ShouldBe(HotkeyService.Bindings.Select(b => b.Id), ignoreOrder: true,
                    "every hotkey is held by the first window, or by another app");
            }
            finally
            {
                late.Detach();
                holder.Detach();
                first.Close();
                second.Close();
            }
        });
    }

    private static Window HiddenWindow()
    {
        var window = new Window { ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000, Width = 200, Height = 200 };
        window.Show();
        return window;
    }

    [Theory]
    [InlineData("Ctrl+Alt+Y")] // no workspace was opened yet, so there is nothing to toggle to
    [InlineData("Ctrl+Shift+Alt+9")] // no workspace has number 9
    public async Task A_hotkey_with_nothing_to_do_leaves_a_minimised_shell_where_it_is(string label)
    {
        var harness = new ShellTestHarness();
        await harness.Shell.InitializeAsync(CancellationToken.None);
        await harness.Shell.Raven.PendingRefresh; // the microphones are listed off the UI thread

        await OnMinimisedShellAsync(harness, label, window =>
            window.WindowState.ShouldBe(WindowState.Minimized, "the user's own application keeps the foreground"));
    }

    [Fact]
    public async Task A_hotkey_acts_before_the_shell_comes_back_so_the_restore_does_not_dock_the_workspace_it_leaves()
    {
        var harness = new ShellTestHarness();
        await harness.Shell.InitializeAsync(CancellationToken.None);
        await harness.Shell.Raven.PendingRefresh; // the microphones are listed off the UI thread
        harness.VsCodeWindowAppears();
        harness.Shell.Cab.LastHostRect = ScreenRect.FromSize(0, 28, 1600, 900);
        await harness.Shell.EnterCabAsync(harness.App.Id);

        await OnMinimisedShellAsync(harness, "Ctrl+Alt+Y", window =>
        {
            window.WindowState.ShouldBe(WindowState.Normal, "a hotkey that acts brings the shell back");
            harness.Shell.Mode.ShouldBe(ShellMode.Yard);
            harness.Docker.DidNotReceive().Uncloak(500);
        });
    }

    /// <summary>
    /// Minimises a window wired to the shell the way MainWindow is, presses the hotkey, and checks the result on the
    /// window's own thread.
    /// </summary>
    private static Task OnMinimisedShellAsync(ShellTestHarness harness, string label, Action<Window> check)
    {
        var binding = HotkeyService.Bindings.Single(b => b.Label == label);
        return StaThread.RunAsync(() =>
        {
            var window = new Window
            {
                WindowState = WindowState.Minimized, ShowInTaskbar = false, ShowActivated = false,
                Left = -20000, Top = -20000, Width = 200, Height = 200,
            };
            window.Show();
            window.StateChanged += (_, _) => harness.Shell.SetShellMinimized(window.WindowState == WindowState.Minimized);
            harness.Shell.SetShellMinimized(true);
            harness.Docker.ClearReceivedCalls();
            var hwnd = new WindowInteropHelper(window).Handle;
            var hotkeys = new HotkeyService(NullLogger<HotkeyService>.Instance);
            hotkeys.Attach(hwnd, harness.Shell);
            try
            {
                StaThread.SendMessage(hwnd, HotkeyInterop.WmHotkey, binding.Id, 0);

                check(window);
            }
            finally
            {
                hotkeys.Detach();
                window.Close();
            }
        });
    }

    /// <summary>A chat hotkey switches Raven's chat like push to talk works: the minimised shell stays down, no window opens.</summary>
    [Fact]
    public async Task A_chat_hotkey_switches_the_chat_and_leaves_a_minimised_shell_where_it_is()
    {
        var harness = new ShellTestHarness();
        harness.App.Number = 2;
        await harness.Shell.InitializeAsync(CancellationToken.None);
        await harness.Shell.Raven.PendingRefresh;
        var keys = new ChatHotkeys();

        await StaThread.RunAsync(() =>
        {
            var window = new Window
            {
                WindowState = WindowState.Minimized, ShowInTaskbar = false, ShowActivated = false,
                Left = -20000, Top = -20000, Width = 200, Height = 200,
            };
            window.Show();
            var hwnd = new WindowInteropHelper(window).Handle;
            var hotkeys = new HotkeyService(NullLogger<HotkeyService>.Instance, chatHotkeys: keys);
            hotkeys.Attach(hwnd, harness.Shell);
            try
            {
                StaThread.SendMessage(hwnd, HotkeyInterop.WmHotkey, HotkeyService.ChatBaseId + 2, 0); // chat 2

                harness.Shell.Raven.SelectedChat.Label.ShouldBe("2 App");
                window.WindowState.ShouldBe(WindowState.Minimized, "VS Code keeps the focus");
                harness.Shell.Mode.ShouldBe(ShellMode.Yard, "switching never opens the window");

                StaThread.SendMessage(hwnd, HotkeyInterop.WmHotkey, HotkeyService.ChatBaseId + 12, 0); // previous
                harness.Shell.Raven.SelectedChat.ShouldBe(harness.Shell.Raven.YardChat);
            }
            finally
            {
                hotkeys.Detach();
                window.Close();
            }
        });
    }

    /// <summary>A chat hotkey another window holds is marked taken in Settings; let go while a chord box captures keys.</summary>
    [Fact]
    public async Task A_chat_hotkey_held_elsewhere_is_marked_taken_and_capturing_lets_them_go()
    {
        var harness = new ShellTestHarness();
        var keys = new ChatHotkeys();
        var theirs = new ChatHotkeys();

        await StaThread.RunAsync(() =>
        {
            var first = HiddenWindow();
            var second = HiddenWindow();
            var holder = new HotkeyService(NullLogger<HotkeyService>.Instance, chatHotkeys: theirs);
            var late = new HotkeyService(NullLogger<HotkeyService>.Instance, chatHotkeys: keys);
            try
            {
                holder.Attach(new WindowInteropHelper(first).Handle, harness.Shell);
                late.Attach(new WindowInteropHelper(second).Handle, harness.Shell);
                keys.Rows.ShouldAllBe(r => r.Problem == "Taken by another app: pick another chord.");

                theirs.Capturing = true; // the holder lets go while its chord box has the keyboard
                keys.Rows[3].Chord = "Ctrl+Shift+Alt+F3"; // any change registers again, and tries the taken ones once more
                keys.Rows.ShouldAllBe(r => r.Problem == null);
            }
            finally
            {
                late.Detach();
                holder.Detach();
                first.Close();
                second.Close();
            }
        });
    }

    /// <summary>While a chord box has the keyboard, CodeSwitchX's own hotkeys are let go too: pressing one reaches the box.</summary>
    [Fact]
    public async Task Capturing_lets_go_of_the_fixed_hotkeys_too_and_takes_them_back_after()
    {
        var harness = new ShellTestHarness();
        var keys = new ChatHotkeys();

        await StaThread.RunAsync(() =>
        {
            var window = HiddenWindow();
            var other = HiddenWindow();
            var hwnd = new WindowInteropHelper(window).Handle;
            var otherHwnd = new WindowInteropHelper(other).Handle;
            var hotkeys = new HotkeyService(NullLogger<HotkeyService>.Instance, chatHotkeys: keys);
            hotkeys.Attach(hwnd, harness.Shell);
            var toggle = HotkeyService.Bindings.Single(b => b.Label == "Ctrl+Alt+Y");
            try
            {
                keys.Capturing = true;
                HotkeyInterop.Register(otherHwnd, 999, toggle.Modifiers, toggle.VirtualKey).ShouldBeTrue("let go while capturing");
                HotkeyInterop.Unregister(otherHwnd, 999);

                keys.Capturing = false;
                HotkeyInterop.Register(otherHwnd, 999, toggle.Modifiers, toggle.VirtualKey).ShouldBeFalse("taken back after");
            }
            finally
            {
                hotkeys.Detach();
                window.Close();
                other.Close();
            }
        });
    }
}
