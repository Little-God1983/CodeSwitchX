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
            id => _conversations.Contains(id), Path.Combine(_root, "pending"), _time, NullLogger<VsCodeChats>.Instance);
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

        public async Task<CompanionAnswer> SendAsync(CompanionWindow window, string command, CancellationToken ct)
        {
            Commands.Add(command);
            if (Hold is { } hold)
            {
                await hold.Task.WaitAsync(ct);
            }

            if (Answer.Ok)
            {
                NewChat();
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
