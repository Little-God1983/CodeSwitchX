using System.Windows;
using System.Windows.Interop;
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

    [Fact]
    public void Toggle_hotkey_stays_ctrl_alt_y()
    {
        var toggle = HotkeyService.Bindings.Single(b => b.VirtualKey == 0x59);

        (toggle.Modifiers & ~HotkeyModifiers.NoRepeat).ShouldBe(HotkeyModifiers.Control | HotkeyModifiers.Alt);
        toggle.Label.ShouldBe("Ctrl+Alt+Y");
    }

    [Fact]
    public void Push_to_talk_is_ctrl_shift_space_without_autorepeat()
    {
        var talk = HotkeyService.Bindings.Single(b => b.Id == 21);

        talk.Modifiers.ShouldBe(HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.NoRepeat,
            "an autorepeat must not press the mic again while the keys are held");
        talk.VirtualKey.ShouldBe(0x20u);
        talk.Label.ShouldBe("Push to talk");
        talk.Keys.ShouldBe("Ctrl+Shift+Space");
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

        await OnMinimisedShellAsync(harness, "Push to talk", window =>
        {
            harness.Shell.Raven.State.ShouldBe(RavenState.Listening);
            harness.Recorder.Received(1).Start(headset.Id);
            window.WindowState.ShouldBe(WindowState.Minimized, "talking to Raven must not take the foreground from VS Code");
        });
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
    [InlineData("Ctrl+Shift+Alt+9")] // the Yard has one tile
    public async Task A_hotkey_with_nothing_to_do_leaves_a_minimised_shell_where_it_is(string label)
    {
        var harness = new ShellTestHarness();
        await harness.Shell.InitializeAsync(CancellationToken.None);

        await OnMinimisedShellAsync(harness, label, window =>
            window.WindowState.ShouldBe(WindowState.Minimized, "the user's own application keeps the foreground"));
    }

    [Fact]
    public async Task A_hotkey_acts_before_the_shell_comes_back_so_the_restore_does_not_dock_the_workspace_it_leaves()
    {
        var harness = new ShellTestHarness();
        await harness.Shell.InitializeAsync(CancellationToken.None);
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
}
