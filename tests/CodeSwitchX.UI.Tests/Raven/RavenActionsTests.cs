using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Raven;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.UI.Tests.Raven;

public sealed class RavenActionsTests
{
    private static readonly YardWorkspace Diffusion = new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Diffusion-Full", "Apps",
        @"E:\Repos\DiffusionNexus.Installer.SDK", [new("DiffusionNexus", @"E:\Repos\DiffusionNexus")], []);

    private static readonly Workspace Registered = new()
    {
        Id = Diffusion.Id, Name = "Diffusion-Full", RootPath = @"E:\Repos\DiffusionNexus.Installer.SDK",
        WorkspaceFile = @"E:\Repos\Diffusion-Full.code-workspace",
    };

    private readonly FakeVsCode _vsCode = new();
    private readonly ChatSettings _chats = new();
    private readonly FakeShell _shell;
    private readonly List<string> _sequence = [];
    private readonly FakeTimeProvider _time = new();
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly RavenActions _actions;
    private Workspace? _registered = Registered;

    private readonly TurnStops _stops;

    public RavenActionsTests()
    {
        _shell = new FakeShell(_chats);
        _vsCode.Sequence = _sequence;
        _stops = new TurnStops(_bus, _time);
        _actions = new RavenActions(_vsCode, _chats, _bus, (id, workspace) => _sequence.Add($"claim {id} {workspace}"), () => _shell, new ImmediateDispatcher(),
            (id, _) => Task.FromResult(_registered?.Id == id ? _registered : null), _stops, _time, NullLogger<RavenActions>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<VoiceChatView> StartAsync(string? model = null, string? effort = null, YardFolder? folder = null) =>
        _actions.StartChatAsync(Diffusion, folder, model, effort, Ct);

    [Fact]
    public async Task A_chat_opens_in_the_workspace_s_VS_Code_and_is_put_on_its_tile()
    {
        var started = await StartAsync();

        var request = _vsCode.Requests.ShouldHaveSingleItem();
        request.Workspace.ShouldBeSameAs(Registered);
        _sequence.ShouldBe(["start Diffusion-Full", $"claim new-chat {Diffusion.Id}"]);
        started.ShouldBe(new VoiceChatView("new-chat", Diffusion.Id, "Diffusion-Full", @"E:\Repos\DiffusionNexus.Installer.SDK", null, null,
            "diffusionnexus-4f"));
    }

    [Theory]
    [InlineData(SessionState.Ended)]
    [InlineData(SessionState.Errored)]
    public async Task A_chat_Raven_started_that_ends_loses_its_mark(SessionState end)
    {
        await StartAsync("Fable", "high");
        var now = _time.GetUtcNow();

        _bus.Publish(new SessionChanged(ChatNewsTests.Chat("new-chat", SessionState.Idle, now), ChatNewsTests.Chat("new-chat", SessionState.Working, now)));
        _actions.StartedByRaven("new-chat").ShouldBeTrue("working is no end");
        _bus.Publish(new SessionChanged(ChatNewsTests.Chat("new-chat", SessionState.Working, now), ChatNewsTests.Chat("new-chat", end, now)));
        _bus.Publish(new SessionChanged(ChatNewsTests.Chat("other", SessionState.Working, now), ChatNewsTests.Chat("other", end, now)));

        _actions.StartedByRaven("new-chat").ShouldBeFalse();
        _shell.Marks.ShouldBe([("new-chat", "Fable 5.1 · high"), ("new-chat", null)], "a chat Raven did not start is left alone");
    }

    private static YardChat Chat(string id, string title) => new(id, title, Diffusion.Id, Diffusion.Name, SessionState.Idle, false,
        DateTimeOffset.UnixEpoch, "1m", null, null, 0, null, null);

    [Fact]
    public async Task A_closed_chat_leaves_its_tile_at_once_and_is_Raven_s_no_more()
    {
        await StartAsync();

        var said = await _actions.CloseChatAsync(Chat("new-chat", "Fix the upload"), Ct);

        _sequence[^1].ShouldBe("close new-chat");
        _shell.Forgotten.ShouldBe(["new-chat"]);
        _actions.StartedByRaven("new-chat").ShouldBeFalse();
        said.ShouldBe("The Fix the upload chat is closed. Its conversation stays in VS Code's session list, where the user can open it again.");
    }

    private HookEvent Step(string session) => new() { SessionId = session, EventName = "PreToolUse", At = _time.GetUtcNow(), ToolName = "Bash" };

    [Fact]
    public async Task A_stop_the_chat_s_next_step_takes_is_said_to_have_stopped_it()
    {
        var stop = _actions.StopChatAsync(Chat("busy", "Fix the upload"), Ct);
        _stops.Take(Step("busy"), relayHandsItOn: true).ShouldNotBeNull();

        (await stop).ShouldBe("The Fix the upload chat is stopped. It keeps all it did; telling it to continue carries on.");
    }

    [Fact]
    public async Task A_turn_that_ends_before_the_stop_is_said_so()
    {
        var stop = _actions.StopChatAsync(Chat("busy", "Fix the upload"), Ct);
        _bus.Publish(new SessionChanged(null, ChatNewsTests.Chat("busy", SessionState.Idle, _time.GetUtcNow())));

        (await stop).ShouldBe("The Fix the upload chat finished its turn before the stop came.");
    }

    [Fact]
    public async Task A_chat_found_to_run_an_older_relay_while_the_stop_waits_is_said_so()
    {
        var stop = _actions.StopChatAsync(Chat("busy", "Fix the upload"), Ct);
        _stops.Take(Step("busy"), relayHandsItOn: false);

        (await Should.ThrowAsync<YardActionException>(() => stop)).Message.ShouldStartWith("The hooks Claude Code runs are an older CodeSwitchX's");
    }

    [Fact]
    public async Task A_chat_whose_hooks_are_an_older_relay_s_is_not_promised_a_stop()
    {
        _stops.Take(Step("busy"), relayHandsItOn: false);

        (await Should.ThrowAsync<YardActionException>(() => _actions.StopChatAsync(Chat("busy", "Fix the upload"), Ct))).Message
            .ShouldStartWith("The hooks Claude Code runs are an older CodeSwitchX's");
    }

    [Fact]
    public async Task A_stop_not_taken_in_time_lands_at_the_chat_s_next_step()
    {
        var stop = _actions.StopChatAsync(Chat("busy", "Fix the upload"), Ct);
        for (var i = 0; i < 200 && !stop.IsCompleted; i++)
        {
            await Task.Delay(5, Ct);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        (await stop).ShouldStartWith("The Fix the upload chat stops at its next step");
    }

    [Fact]
    public async Task A_chat_that_could_not_be_closed_keeps_its_row()
    {
        _vsCode.Failure = "VS Code did not close the chat: That chat is not in a tab of this VS Code window.";

        (await Should.ThrowAsync<YardActionException>(() => _actions.CloseChatAsync(Chat("by-hand", "Docs"), Ct))).Message.ShouldBe(_vsCode.Failure);

        _shell.Forgotten.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_chat_counts_as_Raven_s_and_its_row_is_marked_with_how_it_started()
    {
        _actions.StartedByRaven("new-chat").ShouldBeFalse();

        await StartAsync("Fable", "high");

        _actions.StartedByRaven("new-chat").ShouldBeTrue();
        _actions.StartedByRaven("NEW-CHAT").ShouldBeTrue();
        _shell.Marks.ShouldBe([("new-chat", "Fable 5.1 · high")]);
    }

    [Fact]
    public async Task A_folder_the_user_named_is_passed_on()
    {
        await StartAsync(folder: Diffusion.Folders[0]);

        _vsCode.Requests[0].Folder.ShouldBe(@"E:\Repos\DiffusionNexus");
    }

    [Fact]
    public async Task With_nothing_said_it_runs_with_the_defaults()
    {
        _chats.Defaults = new ChatDefaults("Fable", "high");

        await StartAsync();

        (_vsCode.Requests[0].Model, _vsCode.Requests[0].Effort).ShouldBe(("claude-fable-5-1", "high"));
    }

    [Fact]
    public async Task With_no_defaults_it_leaves_them_to_VS_Code()
    {
        await StartAsync();

        (_vsCode.Requests[0].Model, _vsCode.Requests[0].Effort).ShouldBe((null, null));
        _shell.Marks.ShouldBe([("new-chat", "default model · default effort")]);
    }

    [Fact]
    public async Task An_empty_model_or_effort_is_the_default()
    {
        // Tool callers often send "" for an optional parameter they leave out.
        _chats.Defaults = new ChatDefaults("Fable", "high");

        await StartAsync("", " ");
        (await _actions.SetDefaultsAsync("", "low", Ct)).ShouldBe(new ChatDefaults("Fable", "low"));

        (_vsCode.Requests[0].Model, _vsCode.Requests[0].Effort).ShouldBe(("claude-fable-5-1", "high"));
    }

    [Fact]
    public async Task A_model_and_effort_said_for_this_one_are_read_through_the_names()
    {
        _chats.Defaults = new ChatDefaults("Fable", "high");

        await StartAsync("Opus 5.5", "extra high");

        (_vsCode.Requests[0].Model, _vsCode.Requests[0].Effort).ShouldBe(("claude-opus-5-5", "xhigh"));
        _chats.Defaults.ShouldBe(new ChatDefaults("Fable", "high")); // for this one only
    }

    [Theory]
    [InlineData("GPT", null, "'GPT' is no model Raven knows: say Fable, Opus, Sonnet, Haiku, the name with its version, or a full model id. Nothing was changed.")]
    [InlineData(null, "hard", "'hard' is no effort level: say low, medium, high, xhigh, max. Nothing was changed.")]
    public async Task A_model_or_effort_it_does_not_know_is_refused_before_anything_starts(string? model, string? effort, string message)
    {
        (await Should.ThrowAsync<YardActionException>(() => StartAsync(model, effort))).Message.ShouldBe(message);

        _sequence.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_start_that_failed_is_refused_with_its_reason_and_marks_nothing()
    {
        _vsCode.Failure = "VS Code could not open a chat in Diffusion-Full: Wrong token.";

        (await Should.ThrowAsync<YardActionException>(() => StartAsync())).Message.ShouldBe(_vsCode.Failure);

        _sequence.ShouldBe(["start Diffusion-Full"]);
        _shell.Marks.ShouldBeEmpty();
        _actions.StartedByRaven("new-chat").ShouldBeFalse();
    }

    [Fact]
    public async Task A_workspace_gone_from_the_Yard_starts_nothing()
    {
        _registered = null;

        (await Should.ThrowAsync<YardActionException>(() => StartAsync())).Message.ShouldBe("Diffusion-Full is not on the Yard any more.");

        _sequence.ShouldBeEmpty();
    }

    [Fact]
    public async Task Defaults_set_by_voice_go_through_Settings_by_their_name()
    {
        _chats.Defaults = new ChatDefaults(null, "low");

        var set = await _actions.SetDefaultsAsync("opus", null, Ct);

        _shell.Defaults.ShouldBe([new ChatDefaults("Opus", "low")]);
        set.ShouldBe(new ChatDefaults("Opus", "low"));
    }

    [Theory]
    [InlineData("Opus 5.5")]
    [InlineData("claude-opus-5-5")]
    public async Task A_default_said_by_its_version_or_id_is_that_model_whatever_the_alias_becomes(string said)
    {
        (await _actions.SetDefaultsAsync(said, null, Ct)).Model.ShouldBe("claude-opus-5-5");

        _chats.Aliases = [new ModelAlias("Opus", "claude-opus-6-0")];

        _chats.DefaultModelId.ShouldBe("claude-opus-5-5");
    }

    /// <summary>set_window (#117) reaches the shell on the UI thread, and what the shell says comes back.</summary>
    [Fact]
    public async Task The_window_request_reaches_the_shell_and_its_words_come_back()
    {
        (await _actions.SetWindowAsync(WindowRequest.Minimize, Ct)).ShouldBe("CodeSwitchX: Minimize.");

        _shell.Windows.ShouldBe([WindowRequest.Minimize]);
    }

    [Fact]
    public async Task An_effort_alone_keeps_the_model()
    {
        _chats.Defaults = new ChatDefaults("Fable", null);

        (await _actions.SetDefaultsAsync(null, "maximum", Ct)).ShouldBe(new ChatDefaults("Fable", "max"));
    }

    [Fact]
    public async Task A_default_it_does_not_know_changes_nothing()
    {
        await Should.ThrowAsync<YardActionException>(() => _actions.SetDefaultsAsync("GPT", "high", Ct));

        _shell.Defaults.ShouldBeEmpty();
    }

    [Fact]
    public async Task Opening_a_workspace_shows_it_in_the_Cab()
    {
        (await _actions.OpenWorkspaceAsync(Diffusion, Ct)).ShouldBe("Diffusion-Full is open.");

        _shell.Opened.ShouldBe([Diffusion.Id]);
    }

    [Fact]
    public async Task A_workspace_that_does_not_open_is_said()
    {
        _shell.OpenProblem = "VS Code did not start.";

        var error = await Should.ThrowAsync<YardActionException>(() => _actions.OpenWorkspaceAsync(Diffusion, Ct));

        error.Message.ShouldBe("Diffusion-Full could not be opened: VS Code did not start.");
    }

    [Fact]
    public async Task A_workspace_that_takes_too_long_to_show_is_said()
    {
        _shell.Showing = new TaskCompletionSource<string?>().Task; // VS Code never shows its window
        var open = _actions.OpenWorkspaceAsync(Diffusion, Ct);
        for (var i = 0; i < 200 && !open.IsCompleted; i++)
        {
            await Task.Delay(5, Ct);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        var error = await Should.ThrowAsync<YardActionException>(() => open);

        error.Message.ShouldBe("Diffusion-Full could not be opened: VS Code did not show its window within 90 seconds.");
    }

    /// <summary>#153: a window's chat is muted by its number; chat 0 has no mute, and a number no window has is said so.</summary>
    [Fact]
    public async Task A_chat_is_muted_by_number_and_chat_zero_or_an_unknown_one_is_refused_in_words()
    {
        (await _actions.MuteChatAsync(4, true, Ct)).ShouldBe("Chat 4, Diffusion-Full, muted: True.");

        (await Should.ThrowAsync<YardActionException>(() => _actions.MuteChatAsync(0, true, Ct))).Message.ShouldStartWith("Chat 0 has no mute");
        (await Should.ThrowAsync<YardActionException>(() => _actions.MuteChatAsync(9, true, Ct))).Message.ShouldStartWith("No window has the number 9");
    }

    private sealed class FakeShell(ChatSettings chats) : IRavenShell
    {
        public List<Guid> Opened { get; } = [];
        public string? OpenProblem { get; set; }
        public List<ChatDefaults> Defaults { get; } = [];
        public List<(string, string?)> Marks { get; } = [];

        /// <summary>The showing itself, when a test holds it up; else it is done at once, with <see cref="OpenProblem"/>.</summary>
        public Task<string?>? Showing { get; set; }

        public Task<string?> OpenInCabAsync(Guid workspaceId)
        {
            Opened.Add(workspaceId);
            return Showing ?? Task.FromResult(OpenProblem);
        }

        public void ShowYard()
        {
        }

        public (string Said, Guid? WorkspaceId)? SwitchChat(ChatSwitch target) =>
            target.Number == 4 ? ("Chat 4, Diffusion-Full.", Diffusion.Id) : null;

        public string? MuteChat(int number, bool muted) => number == 4 ? $"Chat 4, Diffusion-Full, muted: {muted}." : null;

        public void SetChatDefaults(ChatDefaults defaults)
        {
            Defaults.Add(defaults);
            chats.Defaults = defaults; // as Settings does
        }

        public void MarkVoice(string sessionId, string? label) => Marks.Add((sessionId, label));

        public List<string> Forgotten { get; } = [];

        public void ForgetChat(string sessionId) => Forgotten.Add(sessionId);

        public List<WindowRequest> Windows { get; } = [];

        public string SetWindow(WindowRequest request)
        {
            Windows.Add(request);
            return $"CodeSwitchX: {request}.";
        }
    }

    private sealed class FakeVsCode : IVsCodeChats
    {
        public List<(Workspace Workspace, string? Folder, string? Model, string? Effort)> Requests { get; } = [];
        public List<string> Sequence { get; set; } = [];
        public string? Failure { get; set; }

        public Task<VsCodeChat> StartAsync(Workspace workspace, string? folder, string? model, string? effort, CancellationToken ct)
        {
            Sequence.Add($"start {workspace.Name}");
            Requests.Add((workspace, folder, model, effort));
            return Failure is { } failure
                ? Task.FromException<VsCodeChat>(new YardActionException(failure))
                : Task.FromResult(new VsCodeChat("new-chat", folder ?? workspace.RootPath, "diffusionnexus-4f"));
        }

        public Task CloseAsync(string sessionId, CancellationToken ct)
        {
            Sequence.Add($"close {sessionId}");
            return Failure is { } failure ? Task.FromException(new YardActionException(failure)) : Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Switch_chat_with_open_says_when_the_window_could_not_be_opened()
    {
        _shell.OpenProblem = "VS Code did not start.";

        var error = await Should.ThrowAsync<YardActionException>(() => _actions.SwitchChatAsync(new ChatSwitch(4, false, Open: true), Ct));

        error.Message.ShouldBe("Chat 4, Diffusion-Full. Its window could not be opened: VS Code did not start.");
        _shell.Opened.ShouldBe([Diffusion.Id]);
    }
}
