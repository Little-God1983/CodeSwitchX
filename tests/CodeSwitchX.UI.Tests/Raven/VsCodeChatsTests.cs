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
    private readonly List<LiveChat> _running = [new(100, "old-chat", "app-01")];
    private readonly Dictionary<int, int> _parents = new() { [100] = Host };
    private readonly List<string> _opened = [];
    private readonly FakeTimeProvider _time = new();
    private readonly VsCodeChats _chats;
    private string? _openFailure;

    public VsCodeChatsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "App"));
        Directory.CreateDirectory(Path.Combine(_root, "Lib"));
        _workspace = new Workspace { Name = "App", RootPath = Path.Combine(_root, "App") };
        _windows.NewChat = () =>
        {
            _running.Add(new LiveChat(200, "new-chat", "app-4f"));
            _parents[200] = Host;
        };
        _chats = new VsCodeChats(_windows, _installer, (w, _) =>
            {
                _opened.Add(w.Name);
                return Task.FromResult(_openFailure);
            },
            () => _running.ToList(), pid => _parents.TryGetValue(pid, out var parent) ? parent : null, _time, NullLogger<VsCodeChats>.Instance);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CompanionWindow Window(params string[] folders) => new(Host, "pipe", "token", folders.Length > 0 ? folders : [_workspace.RootPath], null, "0.1.0");

    [Fact]
    public async Task A_chat_opens_in_the_window_already_showing_the_workspace_and_is_the_new_process_its_host_started()
    {
        _windows.Shown = Window();

        var chat = await _chats.StartAsync(_workspace, null, null, null, Ct);

        chat.ShouldBe(new VsCodeChat("new-chat", _workspace.RootPath, "app-4f"));
        _windows.Commands.ShouldBe(["newChat"]);
        _opened.ShouldBeEmpty("VS Code runs already");
    }

    [Fact]
    public async Task A_chat_another_window_starts_meanwhile_is_not_taken_for_it()
    {
        _windows.Shown = Window();
        _windows.NewChat = () =>
        {
            _running.Add(new LiveChat(150, "someone-elses", "other-11")); // the user opened one in another window
            _parents[150] = 9999;
            _running.Add(new LiveChat(200, "new-chat", "app-4f"));
            _parents[200] = Host;
        };

        (await _chats.StartAsync(_workspace, null, null, null, Ct)).SessionId.ShouldBe("new-chat");
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
        _windows.NewChat = () =>
        {
            var pid = next++;
            _running.Add(new LiveChat(pid, $"chat-{pid}", $"lib-{pid}"));
            _parents[pid] = Host;
        };

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
            _running.Add(new LiveChat(200, "new-chat", "app-4f"));
            _parents[200] = Host;
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
        _windows.NewChat = () =>
        {
            var pid = next++;
            _running.Add(new LiveChat(pid, $"chat-{pid}", $"app-{pid}"));
            _parents[pid] = Host;
        };
        _windows.Hold = new TaskCompletionSource();

        var first = _chats.StartAsync(_workspace, null, null, null, Ct);
        var second = _chats.StartAsync(_workspace, null, null, null, Ct);
        await Task.Delay(50, Ct);
        _windows.Commands.Count.ShouldBe(1, "the second waits until the first has found its chat");
        _windows.Hold.SetResult();

        var ids = new[] { (await first).SessionId, (await second).SessionId };
        ids.ShouldBe(["chat-200", "chat-201"], ignoreOrder: true);
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

        public Task<CompanionInstall> EnsureAsync(IEnumerable<string?> profiles, CancellationToken ct)
        {
            Calls.Add(profiles.ToArray());
            return Task.FromResult(Failure is { } failure ? new CompanionInstall([], [failure]) : new CompanionInstall(["Work"], []));
        }
    }
}
