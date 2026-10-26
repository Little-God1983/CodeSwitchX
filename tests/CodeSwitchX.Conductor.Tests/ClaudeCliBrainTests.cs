using System.Text.Json;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Yard;
using LittleGod.Llm.Abstractions;
using LittleGod.Llm.ClaudeCli;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.Conductor.Tests;

/// <summary>
/// Raven's brain over the package's session (#314): how it sets the session up for its role, and what it tells of what the
/// session streams and reports. The session's own mechanics (the process, echoes, interrupts, restarts, rests) are the
/// package's, and tested there (<c>ClaudeCliSessionTests</c>).
/// </summary>
public sealed class ClaudeCliBrainTests : IDisposable
{
    private static readonly Guid Window3 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "csx-brain-" + Guid.NewGuid().ToString("N")));
    private readonly BrainSettings _settings = new();
    private readonly BrainSessionFile _sessions;
    private readonly UnaskedTurns _unasked = new();
    private readonly AskedChats _asked = new();
    private readonly List<UnaskedTurn> _told = [];
    private FakeClaudeSession _session = null!;

    public ClaudeCliBrainTests()
    {
        _paths.EnsureCreated();
        File.WriteAllText(_paths.McpConfigFile, """
            {"mcpServers":{"codeswitchx":{"type":"http","url":"http://127.0.0.1:5000/mcp","headers":{"Authorization":"Bearer secret"}}}}
            """);
        _sessions = new BrainSessionFile(Path.Combine(_paths.RavenDirectory, "sessions.json"));
        _unasked.Taken += _told.Add;
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A brain on a fake session; a window's chat's by default, with its store, its unasked turns and its question state.</summary>
    private ClaudeCliBrain Brain(BrainRole role = BrainRole.Raven, Guid? window = null, bool chat = true, bool asked = true) =>
        new(_paths, _settings, NullLogger<ClaudeCliBrain>.Instance, (options, cli) => _session = new FakeClaudeSession(options, cli), role,
            chat ? BrainChat.Of(role == BrainRole.Overview ? null : window ?? Window3, _sessions) : null, _unasked, asked ? _asked : null);

    private static async Task<List<BrainEvent>> AskAsync(IConductorBrain brain, string text = "What waits?")
    {
        var events = new List<BrainEvent>();
        await foreach (var e in brain.AskAsync(text, Ct))
        {
            events.Add(e);
        }

        return events;
    }

    private static ClaudeCliInitContent Init(string? yard = "connected", bool send = true) => new("claude-sonnet-5-5",
        yard is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [YardMcp.ServerName] = yard },
        send ? ["SendMessage", "mcp__codeswitchx__list_chats"] : ["mcp__codeswitchx__list_chats"], "dontAsk");

    private void Answer(params AIContent[] contents) => _session.Answer = (_, _) => FakeClaudeSession.Updates(contents);

    [Fact]
    public async Task What_the_session_streams_becomes_the_brain_s_events_in_order()
    {
        var brain = Brain();
        Answer(new ClaudeCliQuestionSentContent(), new TextContent("Two "),
            new FunctionCallContent("t1", "mcp__codeswitchx__list_chats", new Dictionary<string, object?> { ["filter"] = "waiting" }) { InformationalOnly = true },
            new FunctionResultContent("t1", "[]"), new FunctionResultContent("t2", null) { Exception = new InvalidOperationException("no") },
            new TextReasoningContent("thinking"), new TextContent(""), new TextContent("chats."), new ClaudeCliAnswerEndedContent(),
            new UsageContent(new UsageDetails { OutputTokenCount = 3 }));

        var events = await AskAsync(brain);

        events.ShouldBe([new BrainQuestionSent(), new BrainText("Two "), new BrainToolCall("t1", "list_chats", """{"filter":"waiting"}"""),
            new BrainToolResult("t1", Failed: false), new BrainToolResult("t2", Failed: true), new BrainText("chats."), new BrainTextEnded()]);
    }

    [Theory]
    [InlineData("mcp__codeswitchx__list_chats", "list_chats")]
    [InlineData("SendMessage", "SendMessage")]
    [InlineData("mcp__odd", "mcp__odd")]
    [InlineData("mcp__a__b__c", "b__c")]
    public void A_tool_is_told_by_its_own_name_without_the_prefix_Claude_Code_gives_an_MCP_tool(string name, string own) =>
        ClaudeCliBrain.ToolName(name).ShouldBe(own);

    [Fact]
    public async Task A_tool_call_s_arguments_are_told_as_compact_json_as_typed()
    {
        var brain = Brain();
        var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>("""{ "workspace": "Müller & Söhne", "n": 2 }""");
        Answer(new FunctionCallContent("t1", "mcp__codeswitchx__find_workspace", parsed), new FunctionCallContent("t2", "SendMessage", null));

        var calls = (await AskAsync(brain)).OfType<BrainToolCall>().ToList();

        calls[0].Input.ShouldBe("""{"workspace":"Müller & Söhne","n":2}""");
        calls[1].Input.ShouldBe("{}", "a call with no arguments");
    }

    [Theory]
    [InlineData("The Claude CLI gave no answer for 90 s and was stopped.", "Raven's brain gave no answer for 90 s and was stopped.")]
    [InlineData("There's an issue with the selected model (claude-nope).", "Raven's brain could not answer: There's an issue with the selected model (claude-nope).")]
    [InlineData("Claude Code is not installed. Install it (https://claude.com/claude-code), then run `claude` once to sign in.",
        "Raven's brain could not answer: Claude Code is not installed. Install it (https://claude.com/claude-code), then run `claude` once to sign in.")]
    public async Task A_turn_that_fails_says_why_in_Raven_s_words_after_what_it_had_said(string reason, string said)
    {
        var brain = Brain();
        _session.Answer = (_, _) => FakeClaudeSession.Failing(new LlmTimeoutException(reason, "claude-cli"), new ClaudeCliQuestionSentContent(), new TextContent("Hal"));

        var events = await AskAsync(brain);

        events.ShouldBe([new BrainQuestionSent(), new BrainText("Hal"), new BrainFailed(said)]);
    }

    [Fact]
    public async Task A_cancelled_question_is_cancelled_not_failed()
    {
        var brain = Brain();
        _session.Answer = (_, _) => FakeClaudeSession.Failing(new OperationCanceledException(), new ClaudeCliQuestionSentContent());

        await Should.ThrowAsync<OperationCanceledException>(() => AskAsync(brain));
    }

    [Fact]
    public async Task A_reader_that_leaves_the_answer_early_ends_the_session_s_turn()
    {
        var brain = Brain();
        var ended = false;
        _session.Answer = (_, ct) => Endless(ct);

        await foreach (var e in brain.AskAsync("Hi", Ct))
        {
            if (e is BrainText)
            {
                break; // the panel stops reading: the session interrupts the turn as its answer is let go
            }
        }

        ended.ShouldBeTrue();

        async IAsyncEnumerable<ChatResponseUpdate> Endless([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            try
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "Half");
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                ended = true;
            }
        }
    }

    [Fact]
    public async Task A_question_is_asked_of_the_session_at_once_so_a_rest_waiting_then_is_dropped()
    {
        var brain = Brain();

        var answer = brain.AskAsync("Hi", Ct); // asked, not yet read
        brain.Rest();
        brain.WarmUp();

        _session.Asked.ShouldBe(["Hi"]);
        _session.Read.ShouldBeEmpty();
        _session.Rests.ShouldBe(1);
        _session.WarmUps.Count.ShouldBe(1);
        await foreach (var _ in answer)
        {
        }
    }

    [Fact]
    public async Task The_model_and_effort_set_are_what_each_question_and_warm_up_starts_with()
    {
        var brain = Brain();
        await AskAsync(brain);

        _settings.Model = "claude-opus-5-5";
        _settings.Effort = "extra high";
        brain.WarmUp();
        await AskAsync(brain);

        _session.Read[0].Effort.ShouldBe(LlmEffort.Default);
        _session.WarmUps.ShouldHaveSingleItem().Model.ShouldBe("claude-opus-5-5", "set before the warm-up returns: the session reads it when it starts");
        _session.Read[1].Model.ShouldBe("claude-opus-5-5");
        _session.Read[1].Effort.ShouldBe(LlmEffort.XHigh);
    }

    [Theory]
    [InlineData("low", LlmEffort.Low)]
    [InlineData("medium", LlmEffort.Medium)]
    [InlineData("high", LlmEffort.High)]
    [InlineData("xhigh", LlmEffort.XHigh)]
    [InlineData("max", LlmEffort.Max)]
    [InlineData(null, LlmEffort.Default)]
    public void The_effort_set_is_the_session_s_level(string? effort, LlmEffort level) => ClaudeCliBrain.EffortOf(effort).ShouldBe(level);

    [Fact]
    public async Task A_window_chat_s_brain_runs_with_the_user_s_settings_no_hooks_and_only_the_Yard_and_SendMessage()
    {
        _settings.Model = "claude-opus-5-5";
        var brain = Brain();
        await AskAsync(brain);

        var options = _session.Options;
        options.SystemPrompt.ShouldBe(BrainSettings.SystemPrompt);
        options.Cli.Isolation.ShouldBe(ClaudeIsolation.UserSettingsNoHooks, "hooks off, the user's settings kept: as before #314");
        options.Cli.Tools.ShouldBe(["SendMessage"]);
        options.Cli.AllowedTools.ShouldBe(["SendMessage"], "with the Yard's server, which the preset allows");
        options.Cli.DisallowedTools.ShouldBeNull();
        options.Cli.WorkingDirectory.ShouldBe(_paths.RavenDirectory);
        options.KeepConversation.ShouldBeTrue();
        options.FreshConversationPerTurn.ShouldBeFalse();
        options.Silence.ShouldBe(ClaudeCliBrain.Silence);
        options.InterruptTimeout.ShouldBe(ClaudeCliBrain.InterruptTimeout);
        options.NewConversationAfter.ShouldBe(ClaudeCliBrain.QuietReset);
        _session.Read[0].Model.ShouldBe("claude-opus-5-5");
        _session.Cli.KeepApiKeyEnvironment.ShouldBeTrue("a setup may need its key to reach the API, as its settings");
        _session.Cli.ScratchRoot.ShouldBe(ClaudeCliBrain.McpDirectory(_paths), "cleared as the app starts: a crash leaves the token there");

        using var servers = JsonDocument.Parse(_session.Read[0].Servers.ShouldNotBeNull());
        var yard = servers.RootElement.GetProperty(YardMcp.ServerName);
        yard.GetProperty("url").GetString().ShouldBe("http://127.0.0.1:5000/mcp");
        yard.GetProperty("headers").GetProperty("Authorization").GetString().ShouldBe("Bearer secret");
        yard.GetProperty("headers").GetProperty(YardMcp.ChatHeader).GetString().ShouldBe(Window3.ToString("D"), "its tools act on its window");
    }

    [Fact]
    public async Task Chat_zero_s_brain_is_the_overview_on_its_own_model_with_no_tool_that_answers_a_card()
    {
        _settings.Model = "claude-opus-5-5";
        _settings.OverviewModel = "claude-sonnet-5-5";
        var brain = Brain(BrainRole.Overview);
        await AskAsync(brain);

        _session.Read[0].Model.ShouldBe("claude-sonnet-5-5");
        _session.Options.SystemPrompt.ShouldBe(BrainSettings.OverviewPrompt);
        _session.Options.Cli.DisallowedTools.ShouldBe(["mcp__codeswitchx__answer_question", "mcp__codeswitchx__answer_permission"]);
        _session.Options.KeepConversation.ShouldBeTrue("its conversation is kept, as a window chat's is");
        using var servers = JsonDocument.Parse(_session.Read[0].Servers.ShouldNotBeNull());
        servers.RootElement.GetProperty(YardMcp.ServerName).GetProperty("headers").GetProperty(YardMcp.ChatHeader).GetString()
            .ShouldBe(YardMcp.OverviewChat, "the tools show chat 0 no card's text");
    }

    [Fact]
    public async Task A_brain_of_no_chat_has_the_app_s_servers_as_they_are_and_keeps_nothing()
    {
        var brain = Brain(chat: false);
        await AskAsync(brain);

        using var servers = JsonDocument.Parse(_session.Read[0].Servers.ShouldNotBeNull());
        servers.RootElement.GetProperty(YardMcp.ServerName).GetProperty("headers").TryGetProperty(YardMcp.ChatHeader, out _).ShouldBeFalse();
        _session.Options.KeepConversation.ShouldBeFalse();
        _session.Options.ConversationChanged.ShouldBeNull();
        _session.Options.QuestionStateChanged.ShouldBeNull("it names no chat to the Yard");
    }

    [Theory]
    [InlineData(BrainRole.Teller)]
    [InlineData(BrainRole.Summarizer)]
    [InlineData(BrainRole.ChatSummarizer)]
    [InlineData(BrainRole.Recapper)]
    public async Task The_teller_and_the_summarizers_have_no_tools_no_server_and_keep_nothing_from_one_turn_to_the_next(BrainRole role)
    {
        _settings.Model = "claude-opus-5-5";
        _sessions.Save(Window3.ToString("N"), new BrainSession("kept", "claude-opus-5-5", DateTimeOffset.UtcNow));
        var brain = Brain(role);
        Answer(new ClaudeCliQuestionSentContent(), Init(yard: "failed", send: false), new TextContent("Done."));

        var events = await AskAsync(brain);

        events.OfType<BrainNotice>().ShouldBeEmpty("it has no tools to report on");
        _session.Restarts.ShouldBe(0);
        var options = _session.Options;
        options.Cli.Tools.ShouldBe([]);
        options.Cli.AllowedTools.ShouldBeNull();
        _session.Read[0].Servers.ShouldBeNull("not even the user's own: strict, with none");
        options.FreshConversationPerTurn.ShouldBeTrue("what one chat said must not reach the next digest or summary");
        options.KeepConversation.ShouldBeFalse();
        options.Conversation.ShouldBeNull();
        options.UnaskedTurn.ShouldBeNull();
        options.QuestionStateChanged.ShouldBeNull();
        options.ConversationChanged.ShouldBeNull();
        _session.Read[0].Model.ShouldBe(role == BrainRole.Teller ? "claude-opus-5-5" : BrainSettings.DefaultOverviewModel);
        options.SystemPrompt.ShouldBe(role switch
        {
            BrainRole.Teller => ClaudeCliBrain.TellerPrompt,
            BrainRole.Summarizer => ClaudeCliBrain.SummarizerPrompt,
            BrainRole.ChatSummarizer => ClaudeCliBrain.ChatSummaryPrompt,
            _ => ClaudeCliBrain.RecapPrompt,
        });
    }

    [Fact]
    public async Task A_chat_s_conversation_is_taken_from_its_store_before_the_first_question_and_kept_there_as_it_changes()
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(-3);
        _sessions.Save(Window3.ToString("N"), new BrainSession("s-1", "claude-sonnet-5-5", at));
        var brain = Brain();

        await AskAsync(brain);
        _session.Read[0].Conversation.ShouldBe(new ClaudeCliConversation("s-1", "claude-sonnet-5-5", at));

        _session.Options.ConversationChanged!(new ClaudeCliConversation("s-2", "claude-opus-5-5", at.AddMinutes(1)));
        new BrainSessionFile(Path.Combine(_paths.RavenDirectory, "sessions.json")).Load(Window3.ToString("N"))
            .ShouldBe(new BrainSession("s-2", "claude-opus-5-5", at.AddMinutes(1)), "kept for the next start, also after an app restart");
        _session.Options.ConversationChanged!(null);
        _sessions.Load(Window3.ToString("N")).ShouldBeNull("forgotten: the next start begins a new one");
    }

    [Fact]
    public async Task The_overview_does_not_pick_up_chat_zero_s_conversation_from_before_it_was_the_overview()
    {
        _sessions.Save("yard", new BrainSession("11111111-0000-0000-0000-000000000000", BrainSettings.DefaultOverviewModel, DateTimeOffset.UtcNow));
        var brain = Brain(BrainRole.Overview);

        await AskAsync(brain);

        _session.Read[0].Conversation.ShouldBeNull("#124: that conversation was told cards and news in full");
    }

    [Theory]
    [InlineData(ClaudeCliNoticeKind.Restarted, "The Claude CLI stopped (exit code 1) and was started again. It has forgotten the conversation so far.",
        "Raven's brain stopped (exit code 1) and was started again. It has forgotten the conversation so far.")]
    [InlineData(ClaudeCliNoticeKind.NewModel, "Now using claude-opus-5-5, starting a new conversation.",
        "Raven now thinks with claude-opus-5-5, starting a new conversation.")]
    [InlineData(ClaudeCliNoticeKind.NewEffort, "Now thinking at extra high effort.", "Raven now thinks at extra high effort.")]
    [InlineData(ClaudeCliNoticeKind.ConversationLost, "The conversation could not be picked up again, so a new one starts.",
        "Raven could not pick this chat's conversation up again, so it starts a new one.")]
    public async Task What_the_session_says_of_itself_is_said_in_Raven_s_words(ClaudeCliNoticeKind kind, string text, string said)
    {
        _settings.Model = "claude-opus-5-5";
        _settings.Effort = "xhigh";
        var brain = Brain();
        Answer(new ClaudeCliSessionNoticeContent(kind, text), new ClaudeCliQuestionSentContent());

        var events = await AskAsync(brain);

        events.ShouldBe([new BrainNotice(said, Warning: false), new BrainQuestionSent()]);
    }

    [Fact]
    public async Task A_brain_of_no_chat_that_lost_its_conversation_says_it_has_forgotten_it()
    {
        var brain = Brain(chat: false);
        Answer(new ClaudeCliSessionNoticeContent(ClaudeCliNoticeKind.ConversationLost, "…without KeepConversation it has forgotten the conversation so far."));

        (await AskAsync(brain)).ShouldBe([new BrainNotice("Raven's brain was started again and has forgotten the conversation so far.", Warning: false)]);
    }

    [Fact]
    public async Task A_Claude_Code_that_sends_no_question_ids_back_is_said_once_for_the_whole_app()
    {
        var window = Brain();
        var windowSession = _session;
        var overview = Brain(BrainRole.Overview);
        var overviewSession = _session;
        windowSession.Answer = (_, _) => FakeClaudeSession.Updates(new ClaudeCliSessionNoticeContent(ClaudeCliNoticeKind.QuestionWithoutId, "…"));
        overviewSession.Answer = (_, _) => FakeClaudeSession.Updates(new ClaudeCliSessionNoticeContent(ClaudeCliNoticeKind.NoEchoes, "…"));

        var first = await AskAsync(window);
        var again = await AskAsync(window);
        var other = await AskAsync(overview);

        first.ShouldBe([new BrainNotice(ClaudeCliBrain.NoIds, Warning: true)]);
        again.ShouldBeEmpty();
        other.ShouldBeEmpty("said once for the whole app: an update of Claude Code is what changes it");
    }

    [Fact]
    public async Task Tools_that_did_not_connect_are_a_warning_once_and_get_a_new_process_a_few_times_in_a_row()
    {
        var brain = Brain();
        Answer(Init(yard: "failed"));

        var turns = new List<List<BrainEvent>>();
        for (var i = 0; i < 3; i++)
        {
            turns.Add(await AskAsync(brain));
        }

        turns[0].ShouldBe([new BrainNotice("Raven cannot see the Yard: its tools did not connect (failed). Its answers can only guess. Raven tries again with the next question.",
            Warning: true)]);
        turns[1].ShouldBeEmpty("said once");
        turns[2].ShouldBeEmpty();
        _session.Restarts.ShouldBe(ClaudeCliBrain.MaxYardRetries, "Claude Code does not connect again by itself, but a few new processes are enough");
    }

    [Fact]
    public async Task Tools_still_connecting_are_no_failure_and_a_failure_after_they_worked_is_told_and_retried_again()
    {
        var brain = Brain();
        Answer(Init(yard: "pending"));
        (await AskAsync(brain)).ShouldBeEmpty();
        Answer(Init(yard: "failed"));
        await AskAsync(brain);
        await AskAsync(brain);
        await AskAsync(brain);
        Answer(Init());
        await AskAsync(brain);
        Answer(Init(yard: "failed"));

        var again = await AskAsync(brain);

        again.ShouldHaveSingleItem().ShouldBeOfType<BrainNotice>().Text.ShouldEndWith("Raven tries again with the next question.");
        _session.Restarts.ShouldBe(ClaudeCliBrain.MaxYardRetries + 1, "connected in between: the retries start over");
    }

    [Fact]
    public async Task Missing_Yard_tools_and_a_Claude_Code_without_SendMessage_are_warnings_once()
    {
        var brain = Brain();
        Answer(Init(yard: null, send: false));

        var first = await AskAsync(brain);
        var again = await AskAsync(brain);

        first.ShouldBe([
            new BrainNotice("Raven cannot see the Yard: its tools are missing. Its answers can only guess. Raven tries again with the next question.", Warning: true),
            new BrainNotice("Raven cannot tell chats in VS Code anything: this Claude Code has no SendMessage tool. Update Claude Code.", Warning: true)]);
        again.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_chat_s_message_folded_into_the_answer_is_noted_with_it()
    {
        var brain = Brain();
        Answer(new ClaudeCliQuestionSentContent(), new ClaudeCliPeerMessageContent(new ClaudeCliPeerMessage("bug-report-1", "Tests pass.")),
            new TextContent("Done."));

        var events = await AskAsync(brain);

        events.ShouldBe([new BrainQuestionSent(), new BrainChatMessage(ClaudeCliBrain.FoldedText("bug-report-1", "Tests pass.")), new BrainText("Done.")]);
    }

    [Fact]
    public void A_folded_message_is_noted_on_one_line_and_cut()
    {
        ClaudeCliBrain.FoldedText(null, "  Line one\n\nline two  ").ShouldEndWith(": \"Line one line two\"");
        ClaudeCliBrain.FoldedText(null, " \n ").ShouldBe("A chat messaged Raven while it answered you, and the answer may speak to it too.");
        var cut = ClaudeCliBrain.FoldedText("bug-report-1", new string('x', 1000));
        cut.ShouldStartWith("The chat \"bug-report-1\" messaged Raven");
        cut.ShouldEndWith(": \"" + new string('x', ClaudeCliBrain.FoldedMessageLength - 1) + "…\"");
        ClaudeCliBrain.FoldedText(" two\nlines \"quoted\" ", "Hi").ShouldStartWith("The chat \"two lines 'quoted'\" messaged");
        ClaudeCliBrain.FoldedText("  ", "Hi").ShouldStartWith("A chat messaged");
    }

    [Fact]
    public void A_turn_the_brain_took_on_its_own_is_told_in_its_window_s_chat_with_what_it_did()
    {
        Brain();

        _session.Options.UnaskedTurn!(new ClaudeCliUnaskedTurn("Started it.",
            [new ClaudeCliToolCall("t1", "mcp__codeswitchx__start_chat", """{"workspace":"App"}""", Failed: false),
                new ClaudeCliToolCall("t2", "SendMessage", "{}", Failed: true)], [], Failure: null));

        var told = _told.ShouldHaveSingleItem();
        told.WorkspaceId.ShouldBe(Window3);
        told.Text.ShouldBe("Started it.");
        told.Calls.ShouldBe([new UnaskedCall(new BrainToolCall("t1", "start_chat", """{"workspace":"App"}"""), Failed: false),
            new UnaskedCall(new BrainToolCall("t2", "SendMessage", "{}"), Failed: true)]);
        told.Failure.ShouldBeNull();
    }

    [Theory]
    [InlineData(ClaudeCliSession.StoppedMidTurn, "its brain stopped in the middle of it")]
    [InlineData("the Claude CLI gave no answer for 90 s and was stopped", "its brain gave no answer for 90 s and was stopped")]
    [InlineData(ClaudeCliSession.CutOff, "it was cut off when a question was cancelled")]
    [InlineData("API Error: overloaded", "API Error: overloaded")]
    public void A_turn_of_its_own_that_failed_says_why_in_Raven_s_words(string failure, string said)
    {
        Brain();

        _session.Options.UnaskedTurn!(new ClaudeCliUnaskedTurn("Half", [], [], failure));

        _told.ShouldHaveSingleItem().Failure.ShouldBe(said);
    }

    [Fact]
    public void A_turn_of_its_own_after_the_brain_went_with_its_chat_is_not_told()
    {
        var brain = Brain();
        brain.Dispose();

        _session.Options.UnaskedTurn!(new ClaudeCliUnaskedTurn("Late", [], [], null));

        _told.ShouldBeEmpty();
    }

    [Fact]
    public void The_brain_says_when_it_is_in_its_user_s_question_and_when_it_cannot_tell_one()
    {
        Brain();
        var chat = YardMcp.ChatKey(Window3, overview: false)!;
        var follow = _session.Options.QuestionStateChanged.ShouldNotBeNull();

        follow(ClaudeCliQuestionState.Asked);
        _asked.IsAsked(chat).ShouldBeTrue("its tools act for its user's question");
        follow(ClaudeCliQuestionState.None);
        _asked.IsAsked(chat).ShouldBeFalse();
        follow(ClaudeCliQuestionState.Unverified);
        _asked.IsUnverified(chat).ShouldBeTrue("#199: refused for that reason");
        _asked.IsAsked(chat).ShouldBeFalse();
        follow(ClaudeCliQuestionState.None);
        _asked.IsUnverified(chat).ShouldBeFalse();
    }

    [Fact]
    public void A_brain_with_nowhere_to_say_it_is_in_a_question_does_not_follow_it()
    {
        Brain(asked: false);

        _session.Options.QuestionStateChanged.ShouldBeNull();
    }

    [Fact]
    public async Task Without_the_MCP_server_the_turn_says_the_Yard_cannot_be_seen_and_nothing_starts()
    {
        var brain = Brain();
        File.Delete(_paths.McpConfigFile);

        var events = await AskAsync(brain);
        brain.WarmUp();

        events.ShouldBe([new BrainFailed($"Raven cannot see the Yard: CodeSwitchX's MCP server did not start ({_paths.McpConfigFile} is missing). Restart CodeSwitchX.")]);
        _session.Read.ShouldBeEmpty();
        _session.WarmUps.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_MCP_config_without_the_Yard_s_server_is_said_for_the_chat()
    {
        var brain = Brain();
        File.WriteAllText(_paths.McpConfigFile, """{"mcpServers":{}}""");

        var events = await AskAsync(brain);

        events.ShouldHaveSingleItem().ShouldBeOfType<BrainFailed>().Reason.ShouldStartWith("Raven cannot see the Yard from this chat: its MCP config could not be read (");
        _session.Read.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_disposed_brain_lets_its_session_go_and_a_late_question_says_it_shut_down()
    {
        var brain = Brain();

        brain.Dispose();
        var events = await AskAsync(brain);

        _session.Disposed.ShouldBeTrue();
        events.ShouldBe([new BrainFailed(ClaudeCliBrain.ShutDown)]);
        _session.Read.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_turn_cut_off_by_the_app_closing_says_it_shut_down()
    {
        var brain = Brain();
        _session.Answer = (_, _) => DisposedMidTurn();

        var events = await AskAsync(brain);

        events.ShouldBe([new BrainQuestionSent(), new BrainFailed(ClaudeCliBrain.ShutDown)]);

        async IAsyncEnumerable<ChatResponseUpdate> DisposedMidTurn()
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new ClaudeCliQuestionSentContent()]);
            await Task.Yield();
            brain.Dispose();
            throw new LlmUnavailableException("The Claude CLI session has been disposed.", "claude-cli", LlmReadinessState.NotRunning);
        }
    }

    [Fact]
    public void A_restart_settle_set_on_the_brain_is_the_session_s()
    {
        var brain = new ClaudeCliBrain(_paths, _settings, NullLogger<ClaudeCliBrain>.Instance, (options, cli) => _session = new FakeClaudeSession(options, cli))
        {
            RestartSettle = TimeSpan.FromSeconds(1),
        };

        _session.Options.RestartSettle.ShouldBe(TimeSpan.FromSeconds(1));
        brain.Dispose();
    }
}
