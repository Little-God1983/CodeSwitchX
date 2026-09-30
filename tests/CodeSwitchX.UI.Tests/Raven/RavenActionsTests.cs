using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Raven;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.UI.Tests.Raven;

public sealed class RavenActionsTests
{
    private static readonly YardWorkspace Diffusion = new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Diffusion-Full", "Apps",
        @"E:\Repos\DiffusionNexus.Installer.SDK", [new("DiffusionNexus", @"E:\Repos\DiffusionNexus")], []);

    private static readonly YardFolder Nexus = Diffusion.Folders[0];

    private readonly FakeAgents _agents = new();
    private readonly ChatSettings _chats = new();
    private readonly FakeShell _shell;
    private readonly List<string> _sequence = [];
    private readonly List<string> _urls = [];
    private string? _urlFailure;
    private readonly RavenActions _actions;

    public RavenActionsTests()
    {
        _shell = new FakeShell(_chats);
        _agents.Sequence = _sequence;
        _actions = new RavenActions(_agents, _chats, (id, workspace) => _sequence.Add($"claim {id} {workspace}"), () => _shell, new ImmediateDispatcher(),
            url =>
            {
                _urls.Add(url);
                return _urlFailure;
            }, new FakeTimeProvider(), NullLogger<RavenActions>.Instance)
        {
            HandOverDelay = TimeSpan.Zero,
        };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<StartedChat> StartAsync(string? model = null, string? effort = null) =>
        _actions.StartChatAsync(Diffusion, Nexus, "Filter the LoRA list by base model.", model, effort, Ct);

    [Fact]
    public async Task A_chat_is_put_on_its_tile_before_it_starts_in_the_folder_the_name_matched()
    {
        await StartAsync();

        var request = _agents.Requests.ShouldHaveSingleItem();
        _sequence.ShouldBe([$"claim {request.Id} {Diffusion.Id}", $"start {request.Id}"]);
        (request.WorkspaceId, request.Workspace, request.Folder).ShouldBe((Diffusion.Id, "Diffusion-Full", @"E:\Repos\DiffusionNexus"));
        request.Prompt.ShouldBe("Filter the LoRA list by base model.");
        Guid.TryParse(request.Id, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task With_nothing_said_it_runs_with_the_defaults()
    {
        _chats.Defaults = new ChatDefaults("Fable", "high");

        await StartAsync();

        (_agents.Requests[0].Model, _agents.Requests[0].Effort).ShouldBe(("claude-fable-5-1", "high"));
    }

    [Fact]
    public async Task With_no_defaults_it_leaves_them_to_Claude_Code()
    {
        await StartAsync();

        (_agents.Requests[0].Model, _agents.Requests[0].Effort).ShouldBe((null, null));
    }

    [Fact]
    public async Task A_model_and_effort_said_for_this_one_are_read_through_the_names()
    {
        _chats.Defaults = new ChatDefaults("Fable", "high");

        await StartAsync("Opus 5.5", "extra high");

        (_agents.Requests[0].Model, _agents.Requests[0].Effort).ShouldBe(("claude-opus-5-5", "xhigh"));
        _chats.Defaults.ShouldBe(new ChatDefaults("Fable", "high")); // for this one only
    }

    [Theory]
    [InlineData("GPT", null, "'GPT' is no model Raven knows. Say Fable, Opus, Sonnet, Haiku, or a full model id.")]
    [InlineData(null, "hard", "'hard' is no effort level. Say low, medium, high, xhigh, max.")]
    public async Task A_model_or_effort_it_does_not_know_is_refused_before_anything_starts(string? model, string? effort, string message)
    {
        (await Should.ThrowAsync<YardActionException>(() => StartAsync(model, effort))).Message.ShouldBe(message);

        _sequence.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_start_that_failed_is_refused_with_its_reason()
    {
        _agents.Failure = "The chat could not start: There's an issue with the selected model.";

        (await Should.ThrowAsync<YardActionException>(() => StartAsync())).Message.ShouldBe(_agents.Failure);
    }

    [Fact]
    public async Task A_model_that_cannot_run_in_auto_mode_comes_with_a_note()
    {
        _agents.Mode = "default";

        var started = await StartAsync("Haiku");

        started.Note.ShouldNotBeNull().ShouldStartWith("Haiku 4.5 cannot run in auto mode, so the chat runs in default mode");
        (await StartAsync("Opus")).Note.ShouldNotBeNull(); // the mode is what counts
        _agents.Mode = "auto";
        (await StartAsync("Opus")).Note.ShouldBeNull();
    }

    [Fact]
    public async Task Defaults_set_by_voice_go_through_Settings_by_their_name()
    {
        _chats.Defaults = new ChatDefaults(null, "low");

        var set = await _actions.SetDefaultsAsync("claude-opus-5-5", null, Ct);

        _shell.Defaults.ShouldBe([new ChatDefaults("Opus", "low")]);
        set.ShouldBe(new ChatDefaults("Opus", "low"));
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

    [Theory]
    [InlineData(false, "Diffusion-Full is open, and the chat opens in VS Code's Claude Code panel.")]
    [InlineData(true, "Diffusion-Full is open, and the chat opens in VS Code's Claude Code panel. It was still working, and that turn was cut off; its input box says to go on.")]
    public async Task Opening_a_chat_Raven_started_stops_it_shows_its_workspace_and_opens_it_in_VS_Code(bool working, string said)
    {
        _agents.Add("dddddddd-0004", Diffusion.Id, working);

        var result = await _actions.OpenWorkspaceAsync(null, "dddddddd", Ct);

        result.ShouldBe(said);
        _sequence.ShouldBe(["stop dddddddd-0004"]);
        _shell.Opened.ShouldBe([Diffusion.Id]);
        _urls.ShouldBe([RavenActions.ChatUrl("dddddddd-0004", working)]);
    }

    [Fact]
    public async Task Opening_a_workspace_by_itself_opens_no_chat()
    {
        (await _actions.OpenWorkspaceAsync(Diffusion, null, Ct)).ShouldBe("Diffusion-Full is open.");

        _urls.ShouldBeEmpty();
        _sequence.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_workspace_that_does_not_open_is_said()
    {
        _shell.OpenProblem = "VS Code did not start.";

        var error = await Should.ThrowAsync<YardActionException>(() => _actions.OpenWorkspaceAsync(Diffusion, null, Ct));

        error.Message.ShouldBe("Diffusion-Full could not be opened: VS Code did not start.");
    }

    [Fact]
    public async Task A_VS_Code_that_cannot_be_asked_to_open_the_chat_is_said()
    {
        _agents.Add("dddddddd-0004", Diffusion.Id, working: false);
        _urlFailure = "VS Code executable not found.";

        var error = await Should.ThrowAsync<YardActionException>(() => _actions.OpenWorkspaceAsync(null, "dddddddd", Ct));

        error.Message.ShouldContain("could not be asked to open the chat: VS Code executable not found.");
    }

    [Fact]
    public async Task An_unknown_chat_without_a_workspace_opens_nothing()
    {
        await Should.ThrowAsync<YardActionException>(() => _actions.OpenWorkspaceAsync(null, "zzzz", Ct));

        _shell.Opened.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true, "The chat in Diffusion-Full is stopped. It was in the middle of its work.")]
    [InlineData(false, "The chat in Diffusion-Full is stopped.")]
    public async Task A_stop_says_whether_it_cut_the_chat_off(bool working, string said)
    {
        _agents.Add("dddddddd-0004", Diffusion.Id, working);

        (await _actions.StopChatAsync("dddddddd", Ct)).ShouldBe(said);
    }

    [Fact]
    public void The_chats_it_runs_mark_their_rows_and_their_failures_reach_the_log()
    {
        var chat = new AgentChat("dddddddd-0004", Diffusion.Id, "Diffusion-Full", @"E:\Repos\DiffusionNexus", "claude-fable-5-1", "high", true, "auto", false);

        _agents.RaiseChanged(chat);
        _agents.RaiseFailed(chat, "Claude Code stopped (exit code 3).");
        _agents.RaiseChanged(chat with { Ended = true });

        _shell.Marks.ShouldBe([("dddddddd-0004", "Fable 5.1 · high"), ("dddddddd-0004", null)]);
        _shell.Warnings.ShouldBe(["Raven's chat in Diffusion-Full (DiffusionNexus): Claude Code stopped (exit code 3)."]);
    }

    [Fact]
    public void The_link_opens_the_session_and_fills_in_go_on_only_for_a_chat_that_was_cut_off()
    {
        RavenActions.ChatUrl("abc", cutOff: false).ShouldBe("vscode://anthropic.claude-code/open?session=abc");
        RavenActions.ChatUrl("abc", cutOff: true).ShouldBe("vscode://anthropic.claude-code/open?session=abc&prompt=Go%20on%20where%20you%20stopped.");
    }

    private sealed class FakeShell(ChatSettings chats) : IRavenShell
    {
        public List<Guid> Opened { get; } = [];
        public string? OpenProblem { get; set; }
        public List<ChatDefaults> Defaults { get; } = [];
        public List<(string, string?)> Marks { get; } = [];
        public List<string> Warnings { get; } = [];

        public Task<string?> OpenInCabAsync(Guid workspaceId)
        {
            Opened.Add(workspaceId);
            return Task.FromResult(OpenProblem);
        }

        public void ShowYard()
        {
        }

        public void SetChatDefaults(ChatDefaults defaults)
        {
            Defaults.Add(defaults);
            chats.Defaults = defaults; // as Settings does
        }

        public void MarkVoice(string sessionId, string? label) => Marks.Add((sessionId, label));

        public void Warn(string text) => Warnings.Add(text);
    }

    private sealed class FakeAgents : IAgentLauncher
    {
        private readonly List<AgentChat> _chats = [];

        public List<AgentRequest> Requests { get; } = [];
        public List<string> Sequence { get; set; } = [];
        public string? Failure { get; set; }
        public string Mode { get; set; } = "auto";

        public IReadOnlyList<AgentChat> Chats => _chats;

        public event Action<AgentChat>? Changed;
        public event Action<AgentChat, string>? Failed;

        public void Add(string id, Guid workspaceId, bool working) =>
            _chats.Add(new AgentChat(id, workspaceId, "Diffusion-Full", @"E:\Repos\DiffusionNexus", null, null, working, "auto", false));

        public void RaiseChanged(AgentChat chat) => Changed?.Invoke(chat);

        public void RaiseFailed(AgentChat chat, string why) => Failed?.Invoke(chat, why);

        public Task<AgentStart> StartAsync(AgentRequest request, CancellationToken ct)
        {
            Sequence.Add($"start {request.Id}");
            Requests.Add(request);
            var chat = new AgentChat(request.Id, request.WorkspaceId, request.Workspace, request.Folder, request.Model, request.Effort, true, Mode, false);
            return Task.FromResult(new AgentStart(chat, Failure));
        }

        public Task<AgentChat> SendAsync(string chatId, string text, CancellationToken ct) => Task.FromResult(Find(chatId)!);

        public Task<AgentChat> StopAsync(string chatId, CancellationToken ct)
        {
            var chat = Find(chatId) ?? throw new YardActionException("not ours");
            Sequence.Add($"stop {chat.Id}");
            _chats.Remove(chat);
            return Task.FromResult(chat);
        }

        public AgentChat? Find(string idOrPrefix) => _chats.FirstOrDefault(c => c.Id.StartsWith(idOrPrefix, StringComparison.Ordinal));

        public void Dispose()
        {
        }
    }
}
