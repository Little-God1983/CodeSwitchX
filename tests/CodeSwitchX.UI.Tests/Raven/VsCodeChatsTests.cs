using System.IO;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Hosting.VsCode.Companion;
using CodeSwitchX.Ingest.Live;
using CodeSwitchX.UI.Raven;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.UI.Tests.Raven;

public sealed class VsCodeChatsTests : IDisposable
{
    private const int Host = 4000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "csx-vschats-" + Guid.NewGuid().ToString("N"));
    private readonly Workspace _workspace;
    private readonly FakeWindows _windows = new();
    private readonly FakeInstaller _installer = new();
    private readonly Lock _gate = new();
    private readonly List<LiveChat> _running = [];
    private readonly Dictionary<int, int> _parents = [];
    private readonly HashSet<string> _conversations = [];
    private readonly HashSet<string> _outside = [];
    private readonly List<IReadOnlySet<int>?> _skipped = [];
    private readonly List<string> _opened = [];
    private readonly FakeTimeProvider _time = new();
    private readonly DateTimeOffset _startedAt;
    private readonly VsCodeChats _chats;
    private string? _openFailure;

    public VsCodeChatsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "App"));
        Directory.CreateDirectory(Path.Combine(_root, "Lib"));
        _workspace = new Workspace { Name = "App", RootPath = Path.Combine(_root, "App") };
        _startedAt = _time.GetUtcNow();
        Starts(100, "old-chat", Host, _time.GetUtcNow() - TimeSpan.FromHours(1));
        _windows.NewChat = () => Starts(200, "new-chat", Host);
        _windows.Reveal = id => Starts(400, id, Host); // a chat without a tab gets one, and its Claude Code with it
        _chats = new VsCodeChats(_windows, _installer, (w, _) =>
            {
                _opened.Add(w.Name);
                return Task.FromResult(_openFailure);
            },
            skip =>
            {
                lock (_gate)
                {
                    _skipped.Add(skip is null ? null : new HashSet<int>(skip));
                    return _running.Where(c => skip?.Contains(c.Pid) != true).ToList();
                }
            },
            () =>
            {
                lock (_gate)
                {
                    return new Dictionary<int, int>(_parents);
                }
            },
            id => _conversations.Contains(id), id => _outside.Contains(id), Path.Combine(_root, "pending"), _time, NullLogger<VsCodeChats>.Instance);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A claude.exe under <paramref name="parent"/> records itself, started now unless said otherwise.</summary>
    private void Starts(int pid, string sessionId, int parent, DateTimeOffset? at = null)
    {
        lock (_gate)
        {
            _running.Add(new LiveChat(pid, sessionId, $"app-{pid}", (at ?? _time.GetUtcNow()).ToFileTime()));
            _parents[pid] = parent;
        }
    }

    private CompanionWindow Window(params string[] folders) => new(Host, "pipe", "token", folders.Length > 0 ? folders : [_workspace.RootPath], null, "0.1.1");

    [Fact]
    public async Task A_chat_opens_in_the_window_already_showing_the_workspace_and_is_the_new_process_its_host_started()
    {
        _windows.Shown = Window();

        var chat = await _chats.StartAsync(_workspace, null, null, null, Ct);

        chat.ShouldBe(new VsCodeChat("new-chat", _workspace.RootPath, "app-200"));
        _windows.Commands.ShouldBe([CompanionWindows.NewChat]);
        _opened.ShouldBeEmpty("VS Code runs already");
    }

    [Fact]
    public async Task A_chat_another_window_starts_meanwhile_is_not_taken_for_it()
    {
        _windows.Shown = Window();
        _windows.NewChat = () =>
        {
            Starts(150, "someone-elses", 9999); // the user opened one in another window
            Starts(200, "new-chat", Host);
        };

        (await _chats.StartAsync(_workspace, null, null, null, Ct)).SessionId.ShouldBe("new-chat");
    }

    [Fact]
    public async Task A_tab_the_window_restores_with_its_conversation_is_not_taken_for_the_new_chat()
    {
        // VS Code started cold reopens the tabs it had: their claude.exe start under the same host, on conversations on disk.
        _windows.Shown = Window();
        _conversations.Add("restored");
        _windows.NewChat = () =>
        {
            Starts(150, "restored", Host);
            _time.Advance(TimeSpan.FromMilliseconds(100));
            Starts(200, "new-chat", Host);
        };
        var start = _chats.StartAsync(_workspace, null, null, null, Ct);
        await Advance(() => start.IsCompleted);

        (await start).SessionId.ShouldBe("new-chat");
    }

    [Fact]
    public async Task A_tab_whose_process_started_long_before_the_tab_was_opened_is_not_it()
    {
        // Restored before the start, recorded only now: a process that old is no tab opened by this start.
        _windows.Shown = Window();
        _windows.NewChat = () =>
        {
            Starts(150, "restored-empty", Host, _time.GetUtcNow() - VsCodeChats.StartSlack - TimeSpan.FromSeconds(1));
            Starts(200, "new-chat", Host);
        };

        (await _chats.StartAsync(_workspace, null, null, null, Ct)).SessionId.ShouldBe("new-chat");
    }

    [Fact]
    public async Task A_tab_whose_process_starts_long_after_VS_Code_answered_is_still_taken()
    {
        // In a window that just started, the tab's webview and its claude.exe can come well after the command returned.
        _windows.Shown = Window();
        _windows.NewChat = () => { };
        var start = _chats.StartAsync(_workspace, null, null, null, Ct);
        for (var i = 0; i < 400 && _time.GetUtcNow() - _startedAt < TimeSpan.FromSeconds(20); i++)
        {
            await Task.Delay(1, Ct);
            _time.Advance(VsCodeChats.Poll);
        }

        Starts(200, "new-chat", Host);
        await Advance(() => start.IsCompleted);

        (await start).SessionId.ShouldBe("new-chat");
    }

    [Fact]
    public async Task After_a_late_install_the_companion_gets_its_full_wait_to_start()
    {
        // The install waited behind the one at startup until the wait was nearly over: the companion comes a moment later.
        _windows.Installer = _installer;
        _installer.Took = () => _time.Advance(VsCodeChats.CompanionWait - VsCodeChats.InstallAfter - TimeSpan.FromSeconds(1));
        var start = _chats.StartAsync(_workspace, null, null, null, Ct);
        for (var i = 0; i < 400 && _installer.Calls.Count == 0; i++)
        {
            await Task.Delay(1, Ct);
            _time.Advance(VsCodeChats.Poll);
        }

        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(1, Ct);
            _time.Advance(VsCodeChats.Poll); // 5 s more: past the first wait, within the second
        }

        start.IsCompleted.ShouldBeFalse();
        _windows.Shown = Window();
        await Advance(() => start.IsCompleted);

        (await start).SessionId.ShouldBe("new-chat");
    }

    [Fact]
    public async Task Of_two_empty_tabs_opened_meanwhile_the_newer_is_taken()
    {
        _windows.Shown = Window();
        _windows.NewChat = () =>
        {
            Starts(150, "by-hand", Host, _time.GetUtcNow() - TimeSpan.FromSeconds(1));
            Starts(200, "new-chat", Host);
        };

        (await _chats.StartAsync(_workspace, null, null, null, Ct)).SessionId.ShouldBe("new-chat");
    }

    [Fact]
    public async Task A_chat_whose_record_comes_late_is_waited_for_and_records_seen_are_not_read_again()
    {
        _windows.Shown = Window();
        _windows.NewChat = () => Starts(150, "restored", Host); // with a conversation, below
        _conversations.Add("restored");
        var start = _chats.StartAsync(_workspace, null, null, null, Ct);
        for (var i = 0; i < 200 && SkippedCount() < 3; i++)
        {
            await Task.Delay(5, Ct);
            _time.Advance(VsCodeChats.Poll);
        }

        Starts(200, "new-chat", Host);
        await Advance(() => start.IsCompleted);

        (await start).SessionId.ShouldBe("new-chat");
        _skipped[0].ShouldBeNull("the first read takes in every chat there is");
        _skipped[^1].ShouldNotBeNull().ShouldBe([100, 150], ignoreOrder: true);
    }

    [Fact]
    public async Task VS_Code_that_does_not_run_is_opened_first_and_its_companion_waited_for()
    {
        _openedWindowShowsAfter = 3;
        var start = _chats.StartAsync(_workspace, null, null, null, Ct);
        await Advance(() => start.IsCompleted);

        (await start).SessionId.ShouldBe("new-chat");
        _opened.ShouldBe(["App"]);
        _installer.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_window_without_the_companion_gets_it_installed_into_its_profile()
    {
        _workspace.VsCodeProfile = "Work";
        _windows.ShowAfterInstall = Window();
        _windows.Installer = _installer;
        var start = _chats.StartAsync(_workspace, null, null, null, Ct);
        await Advance(() => start.IsCompleted);

        (await start).SessionId.ShouldBe("new-chat");
        _installer.Calls.ShouldBe([["Work"]]);
    }

    [Fact]
    public async Task A_companion_that_cannot_be_installed_is_said()
    {
        _installer.Failure = "Work: VS Code was not found.";
        var start = _chats.StartAsync(_workspace, null, null, null, Ct);
        await Advance(() => start.IsCompleted);

        (await Should.ThrowAsync<YardActionException>(() => start)).Message
            .ShouldBe("The CodeSwitchX companion could not be installed into VS Code (Work: VS Code was not found.), so no chat can be opened there.");
    }

    [Fact]
    public async Task A_window_whose_companion_never_comes_is_said()
    {
        var start = _chats.StartAsync(_workspace, null, null, null, Ct);
        await Advance(() => start.IsCompleted);

        (await Should.ThrowAsync<YardActionException>(() => start)).Message.ShouldStartWith("The VS Code window of App does not run the CodeSwitchX companion.");
        _windows.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task VS_Code_that_cannot_be_opened_is_said()
    {
        _openFailure = "VS Code executable not found.";

        (await Should.ThrowAsync<YardActionException>(() => _chats.StartAsync(_workspace, null, null, null, Ct))).Message
            .ShouldBe("VS Code could not be opened for App: VS Code executable not found.");
    }

    [Fact]
    public async Task A_companion_that_says_no_is_said()
    {
        _windows.Shown = Window();
        _windows.Answer = new CompanionAnswer(false, Error: "Claude Code's VS Code extension is not installed in this window.");

        (await Should.ThrowAsync<YardActionException>(() => _chats.StartAsync(_workspace, null, null, null, Ct))).Message
            .ShouldBe("VS Code could not open a chat in App: Claude Code's VS Code extension is not installed in this window.");
    }

    [Fact]
    public async Task A_tab_whose_Claude_Code_never_starts_is_said()
    {
        _windows.Shown = Window();
        _windows.NewChat = () => { };
        var start = _chats.StartAsync(_workspace, null, null, null, Ct);
        await Advance(() => start.IsCompleted);

        (await Should.ThrowAsync<YardActionException>(() => start)).Message.ShouldStartWith("VS Code opened a chat tab in App, but its Claude Code did not start within 30 seconds.");
    }

    [Fact]
    public async Task A_chat_runs_in_the_window_s_first_folder()
    {
        var lib = Path.Combine(_root, "Lib");
        _windows.Shown = Window(lib, _workspace.RootPath);
        var next = 200;
        _windows.NewChat = () => Starts(next, $"chat-{next++}", Host);

        (await _chats.StartAsync(_workspace, null, null, null, Ct)).Folder.ShouldBe(lib);
        (await _chats.StartAsync(_workspace, lib.ToUpperInvariant() + "\\", null, null, Ct)).Folder.ShouldBe(lib);
    }

    [Fact]
    public async Task A_folder_VS_Code_does_not_start_chats_in_is_refused_before_anything_opens()
    {
        _windows.Shown = Window(Path.Combine(_root, "Lib"), _workspace.RootPath);

        (await Should.ThrowAsync<YardActionException>(() => _chats.StartAsync(_workspace, _workspace.RootPath, null, null, Ct))).Message
            .ShouldBe("VS Code starts new chats in Lib, the first folder of App; it cannot start one in App.");
        _windows.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task Model_and_effort_are_in_the_folder_s_settings_while_the_tab_opens_and_gone_after()
    {
        _windows.Shown = Window();
        var file = StartSettings.FileIn(_workspace.RootPath);
        string? whileOpening = null;
        _windows.NewChat = () =>
        {
            whileOpening = File.ReadAllText(file);
            Starts(200, "new-chat", Host);
        };

        await _chats.StartAsync(_workspace, null, "claude-opus-5-5", "high", Ct);

        whileOpening.ShouldNotBeNull().ShouldContain("\"model\": \"claude-opus-5-5\"");
        whileOpening.ShouldContain("\"effortLevel\": \"high\"");
        File.Exists(file).ShouldBeFalse();
        Directory.Exists(Path.GetDirectoryName(file)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_settings_are_put_back_when_the_start_fails()
    {
        _windows.Shown = Window();
        _windows.Answer = new CompanionAnswer(false, Error: "Wrong token.");

        await Should.ThrowAsync<YardActionException>(() => _chats.StartAsync(_workspace, null, "claude-opus-5-5", null, Ct));

        File.Exists(StartSettings.FileIn(_workspace.RootPath)).ShouldBeFalse();
    }

    [Fact]
    public async Task Two_starts_in_one_folder_open_their_tabs_one_after_the_other()
    {
        _windows.Shown = Window();
        var next = 200;
        _windows.NewChat = () => Starts(next, $"chat-{next++}", Host);
        _windows.Hold = new TaskCompletionSource();

        var first = _chats.StartAsync(_workspace, null, null, null, Ct);
        var second = _chats.StartAsync(_workspace, null, null, null, Ct);
        await Task.Delay(50, Ct);
        _windows.Commands.Count.ShouldBe(1, "the second waits until the first has found its chat");
        _windows.Hold.SetResult();

        var ids = new[] { (await first).SessionId, (await second).SessionId };
        ids.ShouldBe(["chat-200", "chat-201"], ignoreOrder: true);
    }

    /// <summary>The chat's claude.exe ends: its record is gone.</summary>
    private void Ends(int pid)
    {
        lock (_gate)
        {
            _running.RemoveAll(c => c.Pid == pid);
        }
    }

    private CompanionWindow Closing() => Window() with { Version = "0.2.0" };

    [Fact]
    public async Task A_chat_is_closed_by_the_window_whose_host_started_it_and_waited_for_until_its_process_is_gone()
    {
        _windows.Shown = Closing();
        Starts(300, "busy-chat", Host);
        var close = _chats.CloseAsync("BUSY-CHAT", Ct);
        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(1, Ct);
            _time.Advance(VsCodeChats.Poll); // a chat cut off in its turn takes a few seconds
        }

        close.IsCompleted.ShouldBeFalse("its Claude Code still runs");
        Ends(300);
        await Advance(() => close.IsCompleted);

        await close;
        _windows.Commands.ShouldBe([CompanionWindows.CloseChat]);
        _windows.SessionIds.ShouldBe(["busy-chat"], "the id as the chat's record has it");
    }

    [Fact]
    public async Task A_chat_not_open_in_a_VS_Code_tab_is_not_closed()
    {
        _windows.Shown = Closing();

        (await Should.ThrowAsync<YardActionException>(() => _chats.CloseAsync("in-a-terminal", Ct))).Message
            .ShouldBe("That chat is not open in a VS Code tab, so there is nothing to close.");
        _windows.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_chat_in_a_window_without_the_companion_is_closed_by_hand()
    {
        _windows.Shown = Closing();
        Starts(300, "elsewhere", 9999);

        (await Should.ThrowAsync<YardActionException>(() => _chats.CloseAsync("elsewhere", Ct))).Message
            .ShouldStartWith("The VS Code window that chat runs in does not run the CodeSwitchX companion");
        _windows.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_window_still_running_a_companion_that_cannot_close_is_told_to_reload()
    {
        _windows.Shown = Window(); // 0.1.1: CodeSwitchX updated it, the window has not loaded the update yet
        Starts(300, "a-chat", Host);

        (await Should.ThrowAsync<YardActionException>(() => _chats.CloseAsync("a-chat", Ct))).Message
            .ShouldContain("Reload that window (Developer: Reload Window)");
        _windows.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_tab_the_companion_cannot_close_is_said()
    {
        _windows.Shown = Closing();
        Starts(300, "in-the-side-bar", Host);
        _windows.Answer = new CompanionAnswer(false, Error: "That chat is not in a tab of this VS Code window; it may be in the side bar. It can be closed there.");

        (await Should.ThrowAsync<YardActionException>(() => _chats.CloseAsync("in-the-side-bar", Ct))).Message
            .ShouldBe("VS Code did not close the chat: That chat is not in a tab of this VS Code window; it may be in the side bar. It can be closed there.");
    }

    private CompanionWindow Showing() => Window() with { Version = "0.3.0" };

    /// <summary>#115: the chat's tab is asked for in the workspace's own window, by the chat's id.</summary>
    [Fact]
    public async Task A_chat_is_shown_by_the_window_of_its_workspace()
    {
        _windows.Shown = Showing();

        await _chats.ShowAsync(_workspace, "old-chat", Ct);

        _windows.Commands.ShouldBe([CompanionWindows.OpenChat]);
        _windows.SessionIds.ShouldBe(["old-chat"]);
        _opened.ShouldBeEmpty("VS Code runs already");
    }

    /// <summary>An ended chat has no process and no tab: it is asked for all the same, and VS Code opens it with its history.</summary>
    [Fact]
    public async Task A_chat_that_runs_nowhere_is_shown_too()
    {
        _windows.Shown = Showing();

        await _chats.ShowAsync(_workspace, "ended-yesterday", Ct);

        _windows.SessionIds.ShouldBe(["ended-yesterday"]);
    }

    /// <summary>For an id it does not find from that window, Claude Code opens a blank chat and says nothing: that is not the chat shown.</summary>
    [Fact]
    public async Task A_tab_that_does_not_start_the_chat_asked_for_is_said()
    {
        _windows.Shown = Showing();
        _windows.Reveal = _ => Starts(400, "a-blank-new-chat", Host);

        var show = _chats.ShowAsync(_workspace, "from-another-folder", Ct);
        await Advance(() => show.IsCompleted);

        (await Should.ThrowAsync<YardActionException>(() => show)).Message
            .ShouldBe("VS Code opened a tab in App, but that chat did not start in it within 30 seconds: Claude Code may not have found its "
                + "conversation from that window. Look at the tab in VS Code.");
    }

    /// <summary>The caller moved on while VS Code started: the start is not cut off, and the chat is not asked for.</summary>
    [Fact]
    public async Task A_show_ended_while_VS_Code_starts_asks_for_no_chat()
    {
        using var moved = new CancellationTokenSource();
        var show = _chats.ShowAsync(_workspace, "old-chat", moved.Token);
        await Advance(() => _windows.Looks >= 3);
        moved.Cancel();
        _windows.Shown = Showing();
        await Advance(() => show.IsCompleted);

        await Should.ThrowAsync<OperationCanceledException>(() => show);
        _opened.ShouldBe(["App"]);
        _windows.Commands.ShouldBeEmpty();
    }

    /// <summary>The same folder open in two windows: the chat's tab is in the other one, and is not opened here a second time.</summary>
    [Fact]
    public async Task A_chat_open_in_another_window_is_not_opened_a_second_time()
    {
        _windows.Shown = Showing();
        Starts(300, "elsewhere", 9999);

        (await Should.ThrowAsync<YardActionException>(() => _chats.ShowAsync(_workspace, "ELSEWHERE", Ct))).Message
            .ShouldBe("That chat is open in another VS Code window, not the one of App. Look for its tab there.");
        _windows.Commands.ShouldBeEmpty();
    }

    /// <summary>A chat in a terminal or run by a script is live there: a tab of it would be a second Claude Code on one conversation.</summary>
    [Fact]
    public async Task A_chat_that_runs_outside_VS_Code_is_not_opened_in_a_tab_too()
    {
        _outside.Add("in-a-terminal");

        (await Should.ThrowAsync<YardActionException>(() => _chats.ShowAsync(_workspace, "in-a-terminal", Ct))).Message
            .ShouldStartWith("That chat runs outside VS Code's chat tabs right now");
        _opened.ShouldBeEmpty("VS Code is not started for it");
        _windows.Commands.ShouldBeEmpty();
    }

    /// <summary>The chat is in another window: reloading this one, which an old companion asks for, would not help.</summary>
    [Fact]
    public async Task A_chat_in_another_window_is_said_before_an_old_companion_is()
    {
        _windows.Shown = Closing();
        Starts(300, "elsewhere", 9999);

        (await Should.ThrowAsync<YardActionException>(() => _chats.ShowAsync(_workspace, "elsewhere", Ct))).Message
            .ShouldStartWith("That chat is open in another VS Code window");
    }

    /// <summary>A companion whose version cannot be read is asked: it says itself what it has no command for.</summary>
    [Fact]
    public async Task A_window_whose_companion_says_no_version_is_asked()
    {
        _windows.Shown = Window() with { Version = null };

        await _chats.ShowAsync(_workspace, "old-chat", Ct);

        _windows.Commands.ShouldBe([CompanionWindows.OpenChat]);
    }

    [Fact]
    public async Task Showing_a_chat_starts_VS_Code_when_it_does_not_run()
    {
        var show = _chats.ShowAsync(_workspace, "old-chat", Ct);
        await Advance(() => _windows.Looks >= 3); // its companion takes a moment to start
        _windows.Shown = Showing();
        await Advance(() => show.IsCompleted);

        await show;
        _opened.ShouldBe(["App"]);
        _windows.Commands.ShouldBe([CompanionWindows.OpenChat]);
    }

    [Fact]
    public async Task A_window_still_running_a_companion_that_cannot_show_a_chat_is_told_to_reload()
    {
        _windows.Shown = Closing(); // 0.2.0: CodeSwitchX updated it, the window has not loaded the update yet

        (await Should.ThrowAsync<YardActionException>(() => _chats.ShowAsync(_workspace, "old-chat", Ct))).Message
            .ShouldBe("The VS Code window of App still runs an older CodeSwitchX companion, which cannot show a chat. "
                + "Reload that window (Developer: Reload Window) and try again.");
        _windows.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_chat_VS_Code_does_not_show_is_said()
    {
        _windows.Shown = Showing();
        _windows.Answer = new CompanionAnswer(false, Error: "Claude Code's VS Code extension is not installed in this window.");

        (await Should.ThrowAsync<YardActionException>(() => _chats.ShowAsync(_workspace, "old-chat", Ct))).Message
            .ShouldBe("VS Code did not show the chat: Claude Code's VS Code extension is not installed in this window.");
    }

    [Fact]
    public async Task A_chat_whose_Claude_Code_outlives_its_tab_is_said()
    {
        _windows.Shown = Closing();
        Starts(300, "stuck", Host);
        var close = _chats.CloseAsync("stuck", Ct);
        await Advance(() => close.IsCompleted);

        (await Should.ThrowAsync<YardActionException>(() => close)).Message
            .ShouldBe("VS Code closed the chat's tab, but its Claude Code still runs after 20 seconds.");
    }

    [Fact]
    public void A_session_has_a_conversation_once_its_transcript_is_in_a_project_folder()
    {
        var projects = Path.Combine(_root, "projects");
        Directory.CreateDirectory(Path.Combine(projects, "e--Repos-App"));
        File.WriteAllText(Path.Combine(projects, "e--Repos-App", "with-one.jsonl"), "{}");

        VsCodeChats.HasConversation(projects, "with-one").ShouldBeTrue();
        VsCodeChats.HasConversation(projects, "new-one").ShouldBeFalse();
        VsCodeChats.HasConversation(Path.Combine(_root, "missing"), "with-one").ShouldBeFalse();
    }

    private int SkippedCount()
    {
        lock (_gate)
        {
            return _skipped.Count;
        }
    }

    private int _openedWindowShowsAfter = -1;

    /// <summary>Moves the clock a poll at a time until the condition holds; a window set to show after some looks shows then.</summary>
    private async Task Advance(Func<bool> condition)
    {
        for (var i = 0; i < 2000 && !condition(); i++)
        {
            if (_openedWindowShowsAfter >= 0 && _windows.Looks >= _openedWindowShowsAfter && _windows.Shown is null)
            {
                _windows.Shown = Window();
            }

            await Task.Delay(1, Ct);
            _time.Advance(VsCodeChats.Poll);
        }

        condition().ShouldBeTrue();
    }

    private sealed class FakeWindows : ICompanionWindows
    {
        public CompanionWindow? Shown { get; set; }

        /// <summary>The window that shows once the installer has run.</summary>
        public CompanionWindow? ShowAfterInstall { get; set; }

        public FakeInstaller? Installer { get; set; }

        public int Looks { get; private set; }

        public List<string> Commands { get; } = [];

        public CompanionAnswer Answer { get; set; } = new(true, Host);

        public Action NewChat { get; set; } = () => { };

        /// <summary>What asking for a chat's tab does, with the chat's id; set by the test.</summary>
        public Action<string> Reveal { get; set; } = _ => { };

        /// <summary>When set, a command waits for it: the tab is opening.</summary>
        public TaskCompletionSource? Hold { get; set; }

        public CompanionWindow? Find(Workspace workspace)
        {
            Looks++;
            if (Shown is null && ShowAfterInstall is not null && Installer?.Calls.Count > 0)
            {
                Shown = ShowAfterInstall;
            }

            return Shown;
        }

        public CompanionWindow? Of(int extensionHost) => Shown?.Pid == extensionHost ? Shown : null;

        public List<string?> SessionIds { get; } = [];

        /// <summary>What closing the tab does; set by the test.</summary>
        public Action Close { get; set; } = () => { };

        public async Task<CompanionAnswer> SendAsync(CompanionWindow window, string command, string? sessionId, CancellationToken ct)
        {
            Commands.Add(command);
            SessionIds.Add(sessionId);
            if (Hold is { } hold)
            {
                await hold.Task.WaitAsync(ct);
            }

            if (Answer.Ok)
            {
                (command == CompanionWindows.CloseChat ? Close : command == CompanionWindows.NewChat ? NewChat : () => Reveal(sessionId!))();
            }

            return Answer;
        }
    }

    private sealed class FakeInstaller : ICompanionInstaller
    {
        public List<string?[]> Calls { get; } = [];

        public string? Failure { get; set; }

        /// <summary>What the install does to the clock: how long it took.</summary>
        public Action Took { get; set; } = () => { };

        public Task<CompanionInstall> EnsureAsync(IEnumerable<string?> profiles, CancellationToken ct)
        {
            Took();
            Calls.Add(profiles.ToArray());
            return Task.FromResult(Failure is { } failure ? new CompanionInstall([], [failure]) : new CompanionInstall(["Work"], []));
        }
    }
}
