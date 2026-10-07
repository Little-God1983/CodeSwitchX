using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Yard;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.Conductor.Tests;

public sealed class ClaudeCliBrainTests : IDisposable
{
    private const string Claude = @"C:\Tools\claude.exe";
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "csx-brain-" + Guid.NewGuid().ToString("N")));
    private readonly BrainSettings _settings = new();
    private readonly FakeLauncher _launcher = new();
    private readonly FakeTimeProvider _time = new();
    private string? _claude = Claude;
    private readonly ClaudeCliBrain _brain;

    public ClaudeCliBrainTests()
    {
        _paths.EnsureCreated();
        File.WriteAllText(_paths.McpConfigFile, "{}");
        _brain = new ClaudeCliBrain(_paths, _settings, _launcher, () => _claude, _time, NullLogger<ClaudeCliBrain>.Instance);
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);

    private Task<List<BrainEvent>> AskAsync(string text) => AskUntilAsync(text, TestContext.Current.CancellationToken);

    /// <summary>The turn's events but <see cref="BrainQuestionSent"/>, which <see cref="A_turn_says_when_its_question_has_gone_in"/> checks.</summary>
    private async Task<List<BrainEvent>> AskUntilAsync(string text, CancellationToken ct)
    {
        var events = new List<BrainEvent>();
        await foreach (var e in _brain.AskAsync(text, ct))
        {
            if (e is not BrainQuestionSent)
            {
                events.Add(e);
            }
        }

        return events;
    }

    [Fact]
    public async Task A_turn_says_when_its_question_has_gone_in()
    {
        var events = new List<BrainEvent>();
        await foreach (var e in _brain.AskAsync("Hi", TestContext.Current.CancellationToken))
        {
            events.Add(e);
            if (e is BrainQuestionSent)
            {
                _launcher.Last.Written.Count.ShouldBe(1, "said once the line is written");
            }
        }

        events[0].ShouldBe(new BrainQuestionSent());
        events.OfType<BrainQuestionSent>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task A_process_that_does_not_take_the_question_is_given_up_instead_of_holding_every_later_turn()
    {
        await AskAsync("One");
        _launcher.Last.WritesHang = true;
        var turn = AskAsync("Two");
        await WaitUntil(() => _launcher.Last.Written.Count == 2);

        // The write times out after Silence, then the turn waits up to 2 s for the exit code: time goes on until it ends.
        _time.Advance(ClaudeCliBrain.Silence);
        for (var i = 0; i < 100 && !turn.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        (await turn).ShouldHaveSingleItem().ShouldBeOfType<BrainFailed>().Reason.ShouldStartWith("Raven's brain stopped before it could take the question");
        _launcher.Started[0].Process.Disposed.ShouldBeTrue();
        Reply(await AskAsync("Three")).ShouldBe("Hi.");
    }

    [Fact]
    public async Task An_interrupt_the_process_does_not_take_stops_it()
    {
        _launcher.Answer = _ => [StreamJson.Init(), StreamJson.Text("Half")];
        using var cancel = new CancellationTokenSource();
        var turn = AskUntilAsync("One", cancel.Token);
        await WaitUntil(() => _launcher.Started.Count == 1 && _launcher.Last.Written.Count == 1);
        _launcher.Last.WritesHang = true;

        await cancel.CancelAsync();
        await WaitUntil(() => _launcher.Last.Written.Count == 2);
        for (var i = 0; i < 100 && !turn.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await Should.ThrowAsync<OperationCanceledException>(() => turn);
        _launcher.Last.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_cancelled_teller_turn_is_stopped_at_once_without_an_interrupt()
    {
        var teller = new ClaudeCliBrain(_paths, _settings, _launcher, () => _claude, _time, NullLogger<ClaudeCliBrain>.Instance, BrainRole.Teller);
        _launcher.Answer = _ => [StreamJson.Init(), StreamJson.Text("Half")];
        using var cancel = new CancellationTokenSource();
        var turn = Task.Run(async () =>
        {
            await foreach (var _ in teller.AskAsync("News", cancel.Token))
            {
            }
        }, TestContext.Current.CancellationToken);
        await WaitUntil(() => _launcher.Started.Count == 1 && _launcher.Last.Written.Count == 1);

        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => turn);
        _launcher.Last.Written.Count.ShouldBe(1, "no interrupt: its conversation is thrown away anyway");
        _launcher.Last.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Resting_stops_a_warmed_up_process()
    {
        _brain.WarmUp();
        await WaitUntil(() => _launcher.Started.Count == 1);

        _brain.Rest();

        await WaitUntil(() => _launcher.Last.Disposed);
    }

    [Fact]
    public async Task The_teller_starts_a_fresh_conversation_for_every_digest()
    {
        var teller = new ClaudeCliBrain(_paths, _settings, _launcher, () => _claude, _time, NullLogger<ClaudeCliBrain>.Instance, BrainRole.Teller);

        await foreach (var _ in teller.AskAsync("News one", TestContext.Current.CancellationToken))
        {
        }

        await foreach (var _ in teller.AskAsync("News two", TestContext.Current.CancellationToken))
        {
        }

        _launcher.Started.Count.ShouldBe(2);
        _launcher.Started.ShouldAllBe(s => s.Process.Disposed && s.Process.Written.Count == 1, "what one digest's chats said is gone before the next");
    }

    [Fact]
    public async Task The_teller_has_no_tools_no_MCP_server_and_a_conversation_of_its_own()
    {
        File.Delete(_paths.McpConfigFile); // it needs none
        var teller = new ClaudeCliBrain(_paths, _settings, _launcher, () => _claude, _time, NullLogger<ClaudeCliBrain>.Instance, BrainRole.Teller);
        _launcher.Answer = _ => [StreamJson.Init("failed"), StreamJson.Text("Done."), StreamJson.Result("Done.")];

        var events = new List<BrainEvent>();
        await foreach (var e in teller.AskAsync("News", TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        events.ShouldBe([new BrainQuestionSent(), new BrainText("Done.")], "no word about tools it does not have");
        var arguments = _launcher.Started[^1].Arguments;
        Value(arguments, "--tools").ShouldBe("");
        Value(arguments, "--mcp-config").ShouldBe("""{"mcpServers":{}}""");
        arguments.ShouldContain("--strict-mcp-config", "the user's own MCP servers are left out too");
        arguments.ShouldNotContain("--allowedTools");
        Value(arguments, "--permission-mode").ShouldBe("dontAsk");
        Value(arguments, "--settings").ShouldBe("""{"disableAllHooks":true}""");
        Value(arguments, "--system-prompt").ShouldBe(ClaudeCliBrain.TellerPrompt);
    }

    private static string Reply(IEnumerable<BrainEvent> events) => string.Concat(events.OfType<BrainText>().Select(t => t.Delta));

    [Fact]
    public async Task A_turn_streams_the_reply_in_pieces()
    {
        var events = await AskAsync("What's waiting on me?");

        events.ShouldBe([new BrainText("H"), new BrainText("i.")]);
    }

    [Fact]
    public async Task The_question_goes_in_as_a_stream_json_user_line()
    {
        await AskAsync("Was wartet auf mich? \"Diffusion\"");

        using var line = JsonDocument.Parse(_launcher.Last.Written.ShouldHaveSingleItem());
        line.RootElement.GetProperty("type").GetString().ShouldBe("user");
        line.RootElement.GetProperty("message").GetProperty("role").GetString().ShouldBe("user");
        line.RootElement.GetProperty("message").GetProperty("content").GetString().ShouldBe("Was wartet auf mich? \"Diffusion\"");
    }

    [Fact]
    public async Task The_brain_can_only_look_at_the_Yard()
    {
        await AskAsync("Hi");

        var (executable, arguments, folder, _) = _launcher.Started.ShouldHaveSingleItem();
        executable.ShouldBe(Claude);
        folder.ShouldBe(_paths.RavenDirectory);
        Directory.Exists(_paths.RavenDirectory).ShouldBeTrue();
        Value(arguments, "--tools").ShouldBe("SendMessage", "the one built-in tool: no Bash, no Read, no Edit");
        Value(arguments, "--allowedTools").ShouldBe("mcp__codeswitchx,SendMessage");
        Value(arguments, "--permission-mode").ShouldBe("dontAsk");
        Value(arguments, "--mcp-config").ShouldBe(_paths.McpConfigFile);
        arguments.ShouldContain("--strict-mcp-config");
        Value(arguments, "--settings").ShouldBe("""{"disableAllHooks":true}""", "no hook fires for the brain's turns");
        arguments.ShouldNotContain("--setting-sources", "the user's other settings (a proxy, an API key helper) are kept");
        arguments.ShouldContain("--no-session-persistence");
        Value(arguments, "--model").ShouldBe(BrainSettings.DefaultModel);
        Value(arguments, "--system-prompt").ShouldBe(BrainSettings.SystemPrompt);
        Value(arguments, "--input-format").ShouldBe("stream-json");
        Value(arguments, "--output-format").ShouldBe("stream-json");
        arguments.ShouldContain("--include-partial-messages");
        arguments.ShouldContain("--replay-user-messages", "a question's echo tells its answer from another turn's lines (#192)");
    }

    [Fact]
    public async Task Tool_calls_and_their_results_come_between_the_pieces_of_the_reply()
    {
        _launcher.Answer = _ =>
        [
            StreamJson.Init(),
            StreamJson.Text("Let me look."),
            StreamJson.ToolUse("t1", "mcp__codeswitchx__list_chats", """{"filter":"needs_me"}"""),
            StreamJson.ToolResult("t1"),
            StreamJson.Text("One chat."),
            StreamJson.Result("One chat."),
        ];

        var events = await AskAsync("What's waiting on me?");

        events.ShouldBe([
            new BrainText("Let me look."),
            new BrainToolCall("t1", "list_chats", """{"filter":"needs_me"}"""),
            new BrainToolResult("t1", false),
            new BrainText("One chat."),
        ]);
    }

    [Fact]
    public async Task One_process_takes_every_turn_so_the_conversation_carries_on()
    {
        await AskAsync("One");
        await AskAsync("Two");

        _launcher.Started.ShouldHaveSingleItem().Process.Written.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Warming_up_starts_the_process_that_the_turn_then_uses()
    {
        _brain.WarmUp();
        await WaitUntil(() => _launcher.Started.Count == 1);

        await AskAsync("Hi");

        _launcher.Started.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_process_that_dies_in_the_middle_of_an_answer_fails_the_turn_and_is_started_again_for_the_next()
    {
        _launcher.Answer = _ => [StreamJson.Init(), StreamJson.Text("Let")];
        var first = AskAsync("One");
        await WaitUntil(() => _launcher.Started.Count == 1 && _launcher.Last.Written.Count == 1);
        _launcher.Last.Die(3);

        var failed = (await first).OfType<BrainFailed>().ShouldHaveSingleItem();
        failed.Reason.ShouldBe("Raven's brain stopped in the middle of an answer (exit code 3). Ask again.");

        _launcher.Answer = StreamJson.Reply("Hi.");
        var next = await AskAsync("Two");

        _launcher.Started.Count.ShouldBe(2);
        next[0].ShouldBe(new BrainNotice(
            "Raven's brain stopped in the middle of an answer (exit code 3) and was started again. It has forgotten the conversation so far.", false));
        Reply(next).ShouldBe("Hi.");
    }

    [Fact]
    public async Task A_process_that_died_between_turns_is_started_again_and_the_turn_says_so()
    {
        await AskAsync("One");
        _launcher.Last.Die(0);

        var next = await AskAsync("Two");

        _launcher.Started.Count.ShouldBe(2);
        next[0].ShouldBeOfType<BrainNotice>().Text.ShouldStartWith("Raven's brain stopped (exit code 0) and was started again.");
        Reply(next).ShouldBe("Hi.");
    }

    [Fact]
    public async Task A_new_model_in_the_settings_starts_a_new_process_with_it()
    {
        await AskAsync("One");
        var first = _launcher.Last;

        _settings.Model = "claude-sonnet-5-5";
        var next = await AskAsync("Two");

        first.Disposed.ShouldBeTrue();
        Value(_launcher.Started[^1].Arguments, "--model").ShouldBe("claude-sonnet-5-5");
        next[0].ShouldBe(new BrainNotice("Raven now thinks with claude-sonnet-5-5, starting a new conversation.", false));
    }

    [Fact]
    public async Task An_effort_in_the_settings_runs_the_brain_at_it_and_keeps_the_conversation()
    {
        // #201: the effort is a flag of the process, so it starts again, and picks the conversation up.
        var (brain, _) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        _launcher.Started[^1].Arguments.ShouldNotContain("--effort", "default leaves it to Claude Code");
        var first = _launcher.Last;

        _settings.Effort = "extra high";
        var events = new List<BrainEvent>();
        await foreach (var e in brain.AskAsync("Two", TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        first.Disposed.ShouldBeTrue();
        Value(_launcher.Started[^1].Arguments, "--effort").ShouldBe("xhigh");
        _launcher.Started[^1].Arguments.ShouldContain("--resume", "the conversation carries on");
        events[0].ShouldBe(new BrainNotice("Raven now thinks at extra high effort.", false));
        Reply(events).ShouldBe("Hi.");

        _settings.Effort = "default";
        events.Clear();
        await foreach (var e in brain.AskAsync("Three", TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        _launcher.Started[^1].Arguments.ShouldNotContain("--effort");
        events[0].ShouldBe(new BrainNotice("Raven now thinks at Claude Code's default effort.", false));
    }

    [Fact]
    public async Task A_warm_up_after_an_effort_change_lets_a_turn_of_its_own_end_first()
    {
        // The mic pressed while a chat's message is being answered: the restart for the new effort waits for that turn.
        var (brain, told) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        _launcher.Last.Emit(StreamJson.Init());
        _launcher.Last.Emit(StreamJson.PeerTaken("Which F keys fail?"));
        _launcher.Last.Emit(StreamJson.Text("The bug report chat asks which F keys fail."));

        // Whichever reads the turn, the watcher or the warm-up, the restart waits for its result.
        _settings.Effort = "medium";
        brain.WarmUp();
        _launcher.Last.Emit(StreamJson.Result("The bug report chat asks which F keys fail."));

        await WaitUntil(() => _launcher.Started.Count == 2);
        told.ShouldHaveSingleItem().Failure.ShouldBeNull("it ended, and was not cut off");
        Value(_launcher.Started[^1].Arguments, "--effort").ShouldBe("medium");
    }

    [Fact]
    public void The_effort_is_a_level_as_said_or_none()
    {
        var settings = new BrainSettings { Effort = "Extra high" };
        settings.Effort.ShouldBe("xhigh");
        settings.Effort = "hard";
        settings.Effort.ShouldBeNull("no level: Claude Code's default");
        settings.Effort = "medium";
        settings.Effort.ShouldBe("medium");
        settings.Effort = null;
        settings.Effort.ShouldBeNull();
    }

    [Fact]
    public async Task Without_Claude_Code_the_turn_says_how_to_get_it()
    {
        _claude = null;

        var failed = (await AskAsync("Hi")).ShouldHaveSingleItem().ShouldBeOfType<BrainFailed>();

        failed.Reason.ShouldContain("Claude Code is not installed");
        _launcher.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_the_MCP_server_the_turn_says_the_Yard_cannot_be_seen()
    {
        File.Delete(_paths.McpConfigFile);

        (await AskAsync("Hi")).ShouldHaveSingleItem().ShouldBeOfType<BrainFailed>().Reason.ShouldStartWith("Raven cannot see the Yard");
        _launcher.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Claude_Code_that_will_not_start_fails_the_turn_with_the_reason()
    {
        _launcher.Failure = new Win32Exception(5, "Access is denied");

        (await AskAsync("Hi")).ShouldHaveSingleItem().ShouldBeOfType<BrainFailed>().Reason
            .ShouldBe($"Raven's brain could not be started from {Claude}: Access is denied");
    }

    [Fact]
    public async Task A_Claude_Code_without_SendMessage_is_a_warning_once()
    {
        _launcher.Answer = _ => [StreamJson.Init(send: false), StreamJson.Text("Hm."), StreamJson.Result("Hm.")];

        var first = await AskAsync("One");
        var second = await AskAsync("Two");

        first[0].ShouldBe(new BrainNotice(
            "Raven cannot tell chats in VS Code anything: this Claude Code has no SendMessage tool. Update Claude Code.", true));
        second.OfType<BrainNotice>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Tools_that_did_not_connect_are_a_warning_once_per_process()
    {
        _launcher.Answer = _ => [StreamJson.Init("failed"), StreamJson.Text("Hm."), StreamJson.Result("Hm.")];

        var first = await AskAsync("One");
        var second = await AskAsync("Two");

        first[0].ShouldBe(new BrainNotice(
            "Raven cannot see the Yard: its tools did not connect (failed). Its answers can only guess. Raven tries again with the next question.", true));
        second.OfType<BrainNotice>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Tools_still_connecting_are_no_failure_and_a_failure_after_they_worked_is_told_again()
    {
        var statuses = new Queue<string>(["pending", "failed", "failed", "connected", "failed"]);
        _launcher.Answer = _ => [StreamJson.Init(statuses.Dequeue()), StreamJson.Text("Hm."), StreamJson.Result("Hm.")];

        var warnings = new List<int>();
        for (var turn = 1; turn <= 5; turn++)
        {
            if ((await AskAsync($"Turn {turn}")).OfType<BrainNotice>().Any(n => n.Warning))
            {
                warnings.Add(turn);
            }
        }

        warnings.ShouldBe([2, 5]);
    }

    [Fact]
    public async Task Tools_that_failed_to_connect_get_a_new_process_a_few_times_in_a_row()
    {
        _launcher.Answer = _ => [StreamJson.Init("failed"), StreamJson.Text("Hm."), StreamJson.Result("Hm.")];

        for (var turn = 1; turn <= 5; turn++)
        {
            await AskAsync($"Turn {turn}");
        }

        _launcher.Started.Count.ShouldBe(1 + ClaudeCliBrain.MaxYardRetries, "the server may really be down: no new process for every turn");
        _launcher.Started.Take(ClaudeCliBrain.MaxYardRetries).ShouldAllBe(s => s.Process.Disposed);
    }

    [Fact]
    public async Task Tools_that_connect_on_a_new_process_reset_the_retries()
    {
        var statuses = new Queue<string>(["failed", "connected", "failed", "failed", "failed"]);
        _launcher.Answer = _ => [StreamJson.Init(statuses.Dequeue()), StreamJson.Result("Hm.")];

        for (var turn = 1; turn <= 5; turn++)
        {
            await AskAsync($"Turn {turn}");
        }

        // 1 failed -> 2 connected (kept) -> 3 failed -> 4 failed -> 5 failed and kept: two retries after the reset.
        _launcher.Started.Count.ShouldBe(4);
    }

    [Fact]
    public async Task A_question_after_a_quiet_spell_starts_a_new_conversation_without_a_word()
    {
        await AskAsync("One");
        _time.Advance(ClaudeCliBrain.QuietReset - TimeSpan.FromSeconds(1));
        await AskAsync("Two");
        _launcher.Started.Count.ShouldBe(1, "a question within the spell carries on the conversation");

        _time.Advance(ClaudeCliBrain.QuietReset);
        var next = await AskAsync("Three");

        _launcher.Started.Count.ShouldBe(2);
        _launcher.Started[0].Process.Disposed.ShouldBeTrue();
        next.OfType<BrainNotice>().ShouldBeEmpty();
        Reply(next).ShouldBe("Hi.");
    }

    [Fact]
    public async Task A_disposed_brain_starts_no_process_for_a_late_warm_up_or_question()
    {
        await _brain.DisposeAsync();

        _brain.WarmUp();
        var events = await AskAsync("Hi");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        _launcher.Started.ShouldBeEmpty();
        events.ShouldHaveSingleItem().ShouldBe(new BrainFailed("Raven's brain has shut down with CodeSwitchX."));
    }

    [Fact]
    public async Task A_place_that_cannot_be_searched_for_Claude_Code_fails_the_turn_with_the_reason()
    {
        var brain = new ClaudeCliBrain(_paths, _settings, _launcher, () => throw new UnauthorizedAccessException("Access to the path is denied."),
            _time, NullLogger<ClaudeCliBrain>.Instance);

        var events = new List<BrainEvent>();
        await foreach (var e in brain.AskAsync("Hi", TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        events.ShouldHaveSingleItem().ShouldBe(new BrainFailed("Raven could not look for Claude Code: Access to the path is denied."));
    }

    [Fact]
    public async Task An_answer_that_failed_says_why_and_keeps_the_process()
    {
        _launcher.Answer = _ => [StreamJson.Init(), StreamJson.ErrorResult];

        var failed = (await AskAsync("Hi")).ShouldHaveSingleItem().ShouldBeOfType<BrainFailed>();
        await AskAsync("Again");

        failed.Reason.ShouldBe("Raven's brain could not answer: API Error: 529 Overloaded");
        _launcher.Started.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_process_that_goes_silent_is_given_up()
    {
        _launcher.Answer = _ => [StreamJson.Init()];
        var turn = AskAsync("Hi");
        await WaitUntil(() => _launcher.Started.Count == 1 && _launcher.Last.Written.Count == 1);

        _time.Advance(ClaudeCliBrain.Silence);

        (await turn).ShouldHaveSingleItem().ShouldBe(new BrainFailed("Raven's brain gave no answer for 90 s. Ask again."));
        _launcher.Last.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_cancelled_turn_is_interrupted_and_the_process_keeps_the_conversation()
    {
        _launcher.Answer = line => StreamJson.IsInterrupt(line)
            ? [StreamJson.InterruptAck(line), StreamJson.InterruptedResult]
            : [StreamJson.Init(), StreamJson.Text("Half")];
        using var cancel = new CancellationTokenSource();
        var turn = AskUntilAsync("One", cancel.Token);
        await WaitUntil(() => _launcher.Started.Count == 1 && _launcher.Last.Written.Count == 1);

        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => turn);
        _launcher.Last.Disposed.ShouldBeFalse("an interrupt ends the turn, not the process, so the brain remembers it");
        StreamJson.IsInterrupt(_launcher.Last.Written[1]).ShouldBeTrue();
        _launcher.Last.Answer = StreamJson.Reply("Hi.");
        var next = await AskAsync("Two");
        next.OfType<BrainNotice>().ShouldBeEmpty("an interrupted turn is no crash");
        Reply(next).ShouldBe("Hi.", "the interrupted turn's result was read with it, not as the next turn's");
        _launcher.Started.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_turn_cancelled_before_its_question_went_in_interrupts_nothing_and_keeps_the_process()
    {
        await AskAsync("One");
        _settings.Model = "claude-sonnet-5-5"; // the next turn says so first, before it sends the question
        await AskAsync("Two");
        _settings.Model = "claude-opus-5-5";
        var process = _launcher.Started.Count;
        using var cancel = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in _brain.AskAsync("Three", cancel.Token))
            {
                await cancel.CancelAsync(); // at the notice: the question has not gone in
            }
        });

        var last = _launcher.Last;
        _launcher.Started.Count.ShouldBe(process + 1);
        last.Written.ShouldBeEmpty("neither the question nor an interrupt went to an idle process");
        last.Disposed.ShouldBeFalse();
        last.Answer = StreamJson.Reply("Hi.");
        Reply(await AskAsync("Four")).ShouldBe("Hi.");
        _launcher.Started.Count.ShouldBe(process + 1);
    }

    [Fact]
    public async Task A_turn_that_does_not_end_on_an_interrupt_stops_the_process_so_its_rest_is_not_read_as_the_next_turn()
    {
        _launcher.Answer = _ => [StreamJson.Init(), StreamJson.Text("Half")];
        using var cancel = new CancellationTokenSource();
        var turn = AskUntilAsync("One", cancel.Token);
        await WaitUntil(() => _launcher.Started.Count == 1 && _launcher.Last.Written.Count == 1);

        await cancel.CancelAsync();
        await WaitUntil(() => _launcher.Last.Written.Count == 2);
        _time.Advance(ClaudeCliBrain.InterruptTimeout);

        await Should.ThrowAsync<OperationCanceledException>(() => turn);
        _launcher.Last.Disposed.ShouldBeTrue();
        _launcher.Answer = StreamJson.Reply("Hi.");
        var next = await AskAsync("Two");
        Reply(next).ShouldBe("Hi.");
        _launcher.Started.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_turn_asked_while_another_runs_waits_for_it()
    {
        _launcher.Answer = _ => [StreamJson.Init(), StreamJson.Text("First")];
        var first = AskAsync("One");
        await WaitUntil(() => _launcher.Started.Count == 1 && _launcher.Last.Written.Count == 1);

        var second = AskAsync("Two");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        _launcher.Last.Written.Count.ShouldBe(1, "the second question waits until the first is answered");

        _launcher.Last.Answer = StreamJson.Reply("Second");
        _launcher.Last.Emit(StreamJson.Result("First"));

        Reply(await first).ShouldBe("First");
        Reply(await second).ShouldBe("Second");
    }

    [Fact]
    public async Task Disposing_stops_the_process()
    {
        await AskAsync("Hi");

        await _brain.DisposeAsync();

        _launcher.Last.Disposed.ShouldBeTrue();
    }

    /// <summary>A brain that tells its turns of its own, of a window's chat; and what it told.</summary>
    private (ClaudeCliBrain Brain, List<UnaskedTurn> Told) Telling(Guid window, AskedChats? asked = null)
    {
        var unasked = new UnaskedTurns();
        var told = new List<UnaskedTurn>();
        unasked.Taken += turn =>
        {
            lock (told)
            {
                told.Add(turn);
            }
        };
        // A window's chat writes its own MCP config from the app's.
        File.WriteAllText(_paths.McpConfigFile, """
            {"mcpServers":{"codeswitchx":{"type":"http","url":"http://127.0.0.1:5000/mcp","headers":{"Authorization":"Bearer secret"}}}}
            """);
        var chat = BrainChat.Of(window, new BrainSessionFile(Path.Combine(_paths.RavenDirectory, "sessions.json")));
        return (new ClaudeCliBrain(_paths, _settings, _launcher, () => _claude, _time, NullLogger<ClaudeCliBrain>.Instance, chat: chat, unasked: unasked,
            asked: asked), told);
    }

    private static async Task<string> ReplyTo(ClaudeCliBrain brain, string text)
    {
        var events = new List<BrainEvent>();
        await foreach (var e in brain.AskAsync(text, TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        return Reply(events);
    }

    /// <summary>What a brain wrote for a chat's message to it on 2026-10-06 (#181), between two of the user's questions.</summary>
    private static readonly string[] ChatAsksRaven =
    [
        StreamJson.Init(), StreamJson.Text("The ContentAutomatorX chat needs a few details: "), StreamJson.Text("which F keys?"),
        StreamJson.AssistantText("The ContentAutomatorX chat needs a few details: which F keys?"), StreamJson.Result("The ContentAutomatorX chat needs a few details: which F keys?"),
    ];

    [Fact]
    public async Task A_turn_the_brain_takes_on_its_own_between_questions_is_told_in_its_window_s_chat()
    {
        var window = Guid.NewGuid();
        var (brain, told) = Telling(window);
        (await ReplyTo(brain, "Start a bug report chat in ContentAutomatorX")).ShouldBe("Hi.");

        foreach (var line in ChatAsksRaven)
        {
            _launcher.Last.Emit(line);
        }

        await WaitUntil(() => { lock (told) { return told.Count == 1; } });
        var turn = told.ShouldHaveSingleItem();
        (turn.WorkspaceId, turn.Text).ShouldBe((window, "The ContentAutomatorX chat needs a few details: which F keys?"));
        turn.Calls.ShouldBeEmpty();
        (await ReplyTo(brain, "What's waiting?")).ShouldBe("Hi.", "its own turn is not read as the next question's answer");
        _launcher.Started.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_turn_of_its_own_that_comes_just_before_a_question_is_read_before_the_question_goes_in()
    {
        var (brain, told) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        foreach (var line in ChatAsksRaven)
        {
            _launcher.Last.Emit(line);
        }

        // Asked at once: whichever comes to it first, the watcher or the question, the lines are its own turn's.
        (await ReplyTo(brain, "Two")).ShouldBe("Hi.");
        (await ReplyTo(brain, "Three")).ShouldBe("Hi.");
        told.ShouldHaveSingleItem().Text.ShouldEndWith("which F keys?");
    }

    /// <summary>
    /// A chat's message to Raven begins a turn just as the question goes in, so its first line comes only after the
    /// question was written (#192): the turn's lines as CLI 2.1.292 wrote them on 2026-10-07, the question's echo after them.
    /// </summary>
    private static IEnumerable<string> ChatTurnAhead(string written, bool folded)
    {
        string[] chat =
        [
            StreamJson.Init(), StreamJson.PeerTaken("Which F keys fail?"), StreamJson.Text("The bug report chat asks which F keys fail."),
            StreamJson.AssistantText("The bug report chat asks which F keys fail."),
        ];
        string[] answer = [StreamJson.Taken(written), StreamJson.Text("Nothing waits on you."), StreamJson.AssistantText("Nothing waits on you.")];
        return folded
            // The question is folded in at the chat's tool call: one turn, one result.
            ? [.. chat, StreamJson.ToolUse("toolu_7", "SendMessage"), StreamJson.ToolResult("toolu_7"), .. answer, StreamJson.Result("Nothing waits on you.")]
            // The question is queued behind the chat's turn: two turns.
            : [.. chat, StreamJson.Result("The bug report chat asks which F keys fail."), StreamJson.Init(), .. answer, StreamJson.Result("Nothing waits on you.")];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_chat_s_turn_that_begins_just_after_the_question_went_in_is_not_read_as_its_answer(bool folded)
    {
        var (brain, told) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        _launcher.Last.Answer = written => ChatTurnAhead(written, folded);

        (await ReplyTo(brain, "What's waiting on me?")).ShouldBe("Nothing waits on you.");

        var turn = told.ShouldHaveSingleItem();
        turn.Text.ShouldBe("The bug report chat asks which F keys fail.");
        turn.Calls.Select(c => c.Call.Tool).ShouldBe(folded ? ["SendMessage"] : []);
        turn.Failure.ShouldBeNull();
        _launcher.Last.Answer = StreamJson.Reply("Hi.");
        (await ReplyTo(brain, "Three")).ShouldBe("Hi.", "nothing of either turn is left for the next question");
    }

    [Fact]
    public async Task A_question_s_echo_is_known_without_its_uuid_or_its_text()
    {
        // A Claude Code that does not echo the uuid back, and gives the text back as blocks: it is no peer's all the same.
        var (brain, told) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        _launcher.Last.Answer = written =>
        {
            var line = JsonNode.Parse(written)!.AsObject();
            line.Remove("uuid");
            line["message"]!["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "What's waiting on me?" });
            return ChatTurnAhead(line.ToJsonString(), folded: false);
        };

        (await ReplyTo(brain, "What's waiting on me?")).ShouldBe("Nothing waits on you.");
        told.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task How_the_tools_stand_is_told_with_the_answer_when_a_chat_s_turn_began_the_process_s_first_turn()
    {
        // The only init is the chat's turn's, and the question is folded into it: it still says the Yard's tools failed.
        var (brain, _) = Telling(Guid.NewGuid());
        _launcher.Answer = written =>
        [
            StreamJson.Init(status: "failed"), StreamJson.PeerTaken("Which F keys fail?"), StreamJson.Text("The bug report chat asks."),
            StreamJson.ToolUse("toolu_7", "SendMessage"), StreamJson.ToolResult("toolu_7"),
            StreamJson.Taken(written), StreamJson.Text("Nothing waits on you."), StreamJson.Result("Nothing waits on you."),
        ];

        var events = new List<BrainEvent>();
        await foreach (var e in brain.AskAsync("What's waiting on me?", TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        Reply(events).ShouldBe("Nothing waits on you.");
        events.OfType<BrainNotice>().ShouldHaveSingleItem().Text.ShouldStartWith("Raven cannot see the Yard");
    }

    [Fact]
    public async Task How_the_tools_stand_is_told_with_the_next_question_when_one_was_cancelled_before_its_echo()
    {
        // #196: the only init said the Yard failed, and the question was cancelled before its echo came.
        var (brain, _) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        string? question = null;
        _launcher.Last.Answer = written =>
        {
            if (StreamJson.IsInterrupt(written))
            {
                return [StreamJson.InterruptAck(written), StreamJson.InterruptedResult];
            }

            question = written;
            return [StreamJson.Init(status: "failed")];
        };
        var first = _launcher.Last;

        await CancelledAsync(brain, "What's waiting on me?", () => question);

        first.Disposed.ShouldBeTrue("its Yard tools failed: it is replaced, as after an answered question");
        var events = new List<BrainEvent>();
        await foreach (var e in brain.AskAsync("Two", TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        events.OfType<BrainNotice>().ShouldHaveSingleItem().Text.ShouldStartWith("Raven cannot see the Yard");
        _launcher.Started.Count.ShouldBe(2);
        Reply(events).ShouldBe("Hi.");
    }

    [Fact]
    public async Task A_chat_s_turn_left_running_by_a_cancelled_question_is_not_cut_off_for_a_failed_Yard()
    {
        // The question waited behind a chat's turn, whose init said the Yard failed: the chat's turn runs to its end, and
        // the process is replaced before the next question, which brings the warning.
        var (brain, told) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        string? question = null;
        _launcher.Last.Answer = written =>
        {
            question = written;
            return [StreamJson.Init(status: "failed"), StreamJson.PeerTaken("Which F keys fail?"), StreamJson.Text("The bug report chat asks")];
        };
        var first = _launcher.Last;

        await CancelledAsync(brain, "What's waiting on me?", () => question);

        first.Disposed.ShouldBeFalse("the chat's turn runs on");
        first.Emit(StreamJson.Result("The bug report chat asks"));
        await WaitUntil(() => Copy(told).Count == 1);
        Copy(told)[0].Failure.ShouldBeNull();
        var events = new List<BrainEvent>();
        await foreach (var e in brain.AskAsync("Two", TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        events.OfType<BrainNotice>().ShouldHaveSingleItem().Text.ShouldBe(
            "Raven cannot see the Yard: its tools did not connect (failed). Its answers can only guess. Raven tries again with the next question.");
        first.Disposed.ShouldBeTrue("replaced before the next question, as the warning says");
        _launcher.Started.Count.ShouldBe(2);
        Reply(events).ShouldBe("Hi.");
    }

    [Fact]
    public async Task A_lost_process_s_tools_are_not_told_of()
    {
        // Its init said the Yard failed, and it went before the question's echo: the next start says it was started again,
        // with no warning about the tools of the process that went.
        var (brain, _) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        _launcher.Last.Answer = _ => [StreamJson.Init(status: "failed")];
        var lost = _launcher.Last;
        var answer = Task.Run(() => ReplyTo(brain, "Two"), TestContext.Current.CancellationToken);
        await WaitUntil(() => lost.Written.Count == 2); // its failed init is in the pipe
        await Task.Delay(100, TestContext.Current.CancellationToken);
        lost.Die(1);
        await answer;

        var events = new List<BrainEvent>();
        await foreach (var e in brain.AskAsync("Three", TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        events.OfType<BrainNotice>().ShouldAllBe(n => !n.Text.StartsWith("Raven cannot see the Yard", StringComparison.Ordinal));
        Reply(events).ShouldBe("Hi.");
    }

    [Fact]
    public async Task A_cancelled_question_taken_in_while_the_next_one_waits_is_not_read_as_its_answer()
    {
        // Cancelled before any echo came, the question stays queued behind the turn the interrupt ended (still_queued, CLI
        // 2.1.292), and is taken in just as the next question goes in: its echo has a uuid the next one does not wait for.
        var (brain, told) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        string? cancelled = null;
        _launcher.Last.Answer = written =>
        {
            if (StreamJson.IsInterrupt(written))
            {
                return [StreamJson.InterruptAck(written), StreamJson.InterruptedResult];
            }

            cancelled = written;
            return [StreamJson.Init()];
        };
        using var cancel = new CancellationTokenSource();
        var turn = Task.Run(async () =>
        {
            await foreach (var _ in brain.AskAsync("What colour is the sky?", cancel.Token))
            {
            }
        }, TestContext.Current.CancellationToken);
        await WaitUntil(() => cancelled is not null);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(turn);

        _launcher.Last.Answer = written =>
        [
            StreamJson.Init(), StreamJson.Taken(cancelled!), StreamJson.Text("The sky is blue."), StreamJson.Result("The sky is blue."),
            StreamJson.Init(), StreamJson.Taken(written), StreamJson.Text("Hi."), StreamJson.Result("Hi."),
        ];
        (await ReplyTo(brain, "Three")).ShouldBe("Hi.");
        told.ShouldBeEmpty("it said what no one waits for, and did nothing");
    }

    /// <summary>Asks <paramref name="text"/> and cancels it once its line went in and the lines written for it were read.</summary>
    private async Task CancelledAsync(ClaudeCliBrain brain, string text, Func<string?> written)
    {
        using var cancel = new CancellationTokenSource();
        var turn = Task.Run(async () =>
        {
            await foreach (var _ in brain.AskAsync(text, cancel.Token))
            {
            }
        }, TestContext.Current.CancellationToken);
        await WaitUntil(() => written() is not null);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(turn);
    }

    private static List<UnaskedTurn> Copy(List<UnaskedTurn> told)
    {
        lock (told)
        {
            return [.. told];
        }
    }

    [Theory]
    [InlineData("cut off")]
    [InlineData("ended")]
    [InlineData("failed")]
    [InlineData("folded")]
    public async Task A_chat_s_turn_the_interrupt_of_a_cancelled_question_ends_is_told(string how)
    {
        // #195: a chat's turn had begun (its init came) when the question went in, and the question is cancelled before
        // either echo: the interrupt ends the chat's turn, which is told, not read away unseen.
        var (brain, told) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        string? question = null;
        _launcher.Last.Answer = written =>
        {
            if (!StreamJson.IsInterrupt(written))
            {
                question = written;
                return [StreamJson.Init()];
            }

            string[] chat = [StreamJson.PeerTaken("Which F keys fail?"), StreamJson.Text("The bug report chat asks")];
            return how == "folded"
                // Folded in at a tool call: the rest of the turn answers the cancelled question, unheard, and nothing stays queued.
                ? [.. chat, StreamJson.Taken(question!), StreamJson.Text(" The sky is blue."), StreamJson.InterruptAck(written), StreamJson.InterruptedResult]
                :
                [
                    .. chat, StreamJson.InterruptAck(written),
                    how switch { "ended" => StreamJson.Result("The bug report chat asks"), "failed" => StreamJson.ErrorResult, _ => StreamJson.InterruptedResult },
                    // The question stays queued (still_queued) and is taken in next: unheard, it does nothing, and is not told.
                    StreamJson.Init(), StreamJson.Taken(question!), StreamJson.Text("The sky is blue."), StreamJson.Result("The sky is blue."),
                ];
        };

        await CancelledAsync(brain, "What colour is the sky?", () => question);

        await WaitUntil(() => Copy(told).Count == 1);
        var turn = Copy(told).ShouldHaveSingleItem();
        (turn.Text, turn.Failure).ShouldBe(("The bug report chat asks", how switch
        {
            "ended" => null,
            "failed" => "API Error: 529 Overloaded",
            _ => ClaudeCliBrain.CutOff,
        }));
        _launcher.Last.Answer = StreamJson.Reply("Hi.");
        (await ReplyTo(brain, "Three")).ShouldBe("Hi.");
        Copy(told).ShouldHaveSingleItem("the cancelled question's own turn is not told");
    }

    [Fact]
    public async Task A_chat_s_message_folded_into_a_cancelled_question_s_turn_is_told()
    {
        // The question's echo came, then a chat's message was folded in at a tool call: the interrupt cuts that off too.
        var (brain, told) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        _launcher.Last.Answer = written => StreamJson.IsInterrupt(written)
            ?
            [
                StreamJson.ToolResult("toolu_2"), StreamJson.PeerTaken("Which F keys fail?"), StreamJson.Text("The bug report chat asks"),
                StreamJson.InterruptAck(written), StreamJson.InterruptedResult,
            ]
            : [StreamJson.Init(), StreamJson.Taken(written), StreamJson.Text("Let me look."), StreamJson.ToolUse("toolu_2", "mcp__codeswitchx__list_chats")];

        // Cancelled once the answer began: the question's echo was read, so its own call is its answer's, not told.
        using var cancel = new CancellationTokenSource();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in brain.AskAsync("What's waiting on me?", cancel.Token))
            {
                if (e is BrainText)
                {
                    await cancel.CancelAsync();
                }
            }
        });

        await WaitUntil(() => Copy(told).Count == 1);
        var turn = Copy(told)[0];
        (turn.Text, turn.Failure).ShouldBe(("The bug report chat asks", ClaudeCliBrain.CutOff));
        turn.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task What_a_cancelled_question_did_before_the_interrupt_ended_it_is_told()
    {
        // Its echo came only while the interrupt was read: its answer is unheard, but its tool call shows, as nothing Raven
        // does goes unseen.
        var (brain, told) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        string? question = null;
        _launcher.Last.Answer = written =>
        {
            if (!StreamJson.IsInterrupt(written))
            {
                question = written;
                return [StreamJson.Init()];
            }

            return
            [
                StreamJson.Taken(question!), StreamJson.Text("Stopping it."),
                StreamJson.ToolUse("toolu_3", "mcp__codeswitchx__stop_chat", """{"chat":"issues"}"""), StreamJson.ToolResult("toolu_3", error: true),
                StreamJson.InterruptAck(written), StreamJson.InterruptedResult,
            ];
        };

        await CancelledAsync(brain, "Stop the issues chat", () => question);

        await WaitUntil(() => Copy(told).Count == 1);
        var turn = Copy(told)[0];
        (turn.Text, turn.Failure).ShouldBe(("", null), "unheard: no words, and no one waits for its answer");
        turn.Calls.ShouldHaveSingleItem().Call.Tool.ShouldBe("stop_chat");
    }

    [Fact]
    public async Task A_cancelled_answer_with_no_echo_is_read_away_unseen()
    {
        // A Claude Code that echoes nothing: the lines the interrupt reads are the question's own, no chat's.
        var (brain, told) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        string? question = null;
        _launcher.Last.Answer = written =>
        {
            if (!StreamJson.IsInterrupt(written))
            {
                question = written;
                return [StreamJson.Init()];
            }

            return [StreamJson.Text("Half"), StreamJson.InterruptAck(written), StreamJson.InterruptedResult];
        };

        await CancelledAsync(brain, "What's waiting on me?", () => question);

        _launcher.Last.Answer = StreamJson.Reply("Hi.");
        (await ReplyTo(brain, "Three")).ShouldBe("Hi.");
        Copy(told).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_question_cancelled_behind_a_chat_s_turn_leaves_that_turn_be_and_is_answered_unheard(bool uuidEchoed)
    {
        // An interrupt would end the chat's turn, which is no question's to end: the question stays queued, and once it
        // is taken in, what it does is shown and what it says is not. Without the uuid echoed, it is the oldest cancelled.
        var (brain, told) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        string? question = null;
        _launcher.Last.Answer = written =>
        {
            question = written;
            return [StreamJson.Init(), StreamJson.PeerTaken("Which F keys fail?"), StreamJson.Text("The bug report chat asks")];
        };
        using var cancel = new CancellationTokenSource();
        var turn = Task.Run(async () =>
        {
            await foreach (var _ in brain.AskAsync("Stop the issues chat", cancel.Token))
            {
            }
        }, TestContext.Current.CancellationToken);
        await WaitUntil(() => question is not null);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(turn);

        var echo = JsonNode.Parse(question!)!.AsObject();
        if (!uuidEchoed)
        {
            echo.Remove("uuid");
        }

        foreach (var line in new[]
        {
            StreamJson.Text(" which F keys fail."), StreamJson.Result(), StreamJson.Init(), StreamJson.Taken(echo.ToJsonString()),
            StreamJson.Text("Stopping it."), StreamJson.ToolUse("toolu_8", "mcp__codeswitchx__stop_chat", """{"chat":"issues"}"""),
            StreamJson.ToolResult("toolu_8"), StreamJson.Text("Done."), StreamJson.Result("Done."),
        })
        {
            _launcher.Last.Emit(line);
        }

        await WaitUntil(() => { lock (told) { return told.Count == 2; } });
        (told[0].Text, told[0].Failure).ShouldBe(("The bug report chat asks which F keys fail.", (string?)null), "told whole, not cut off");
        told[1].Text.ShouldBeEmpty("the cancelled question is not answered aloud");
        told[1].Calls.ShouldHaveSingleItem().Call.Tool.ShouldBe("stop_chat", "what it did is shown");
        _launcher.Last.Written.ShouldNotContain(w => StreamJson.IsInterrupt(w));
        _launcher.Last.Answer = StreamJson.Reply("Hi.");
        (await ReplyTo(brain, "Three")).ShouldBe("Hi.");
    }

    [Fact]
    public async Task The_brain_is_in_its_user_s_question_from_its_echo_until_its_turn_ends()
    {
        // The Yard's tools that act refuse its chat otherwise (#193): a tool call of the question's must find it asked. It
        // is followed as the lines are written, not as the answer is read: the tool calls do not wait for the panel.
        var asked = new AskedChats();
        var window = Guid.NewGuid();
        var chat = window.ToString("D");
        var (brain, _) = Telling(window, asked);
        var seen = new List<bool>();
        IEnumerable<string> Answer(string written)
        {
            yield return StreamJson.Init();
            seen.Add(asked.IsAsked(chat));
            yield return StreamJson.Taken(written);
            seen.Add(asked.IsAsked(chat));
            yield return StreamJson.Text("Hi.");
            yield return StreamJson.Result("Hi.");
            seen.Add(asked.IsAsked(chat));
        }

        _launcher.Answer = Answer;

        (await ReplyTo(brain, "One")).ShouldBe("Hi.");

        seen.ShouldBe([false, true, false], "asked from its echo, and no more once its turn is over");
        asked.IsAsked(chat).ShouldBeFalse();
    }

    [Fact]
    public async Task A_question_echoed_with_no_uuid_is_answered_but_never_acts_and_says_why_once()
    {
        // #199: with no uuid, its echo cannot be told from an earlier, cancelled question's, which must not act.
        var asked = new AskedChats();
        var window = Guid.NewGuid();
        var chat = window.ToString("D");
        var (brain, _) = Telling(window, asked);
        var seen = new List<bool>();
        var unverified = new List<bool>();
        IEnumerable<string> Answer(string written)
        {
            var line = JsonNode.Parse(written)!.AsObject();
            line.Remove("uuid");
            yield return StreamJson.Init();
            yield return StreamJson.Taken(line.ToJsonString());
            seen.Add(asked.IsAsked(chat));
            unverified.Add(asked.IsUnverified(chat));
            yield return StreamJson.Text("Hi.");
            yield return StreamJson.Result("Hi.");
            unverified.Add(asked.IsUnverified(chat));
            if (unverified.Count > 2)
            {
                yield break; // the second question drained the first one's chat turn: nothing is left in the pipe
            }

            // A chat's message after it: its turn is refused as a chat's, not for the missing ids.
            yield return StreamJson.Init();
            yield return StreamJson.PeerTaken("Stop the issues chat.");
            unverified.Add(asked.IsUnverified(chat));
            yield return StreamJson.Result("");
        }

        _launcher.Answer = Answer;
        var events = new List<BrainEvent>();
        foreach (var text in new[] { "One", "Two" })
        {
            await foreach (var e in brain.AskAsync(text, TestContext.Current.CancellationToken))
            {
                events.Add(e);
            }
        }

        Reply(events).ShouldBe("Hi.Hi.", "it is answered");
        seen.ShouldBe([false, false], "it never lets Raven act");
        events.OfType<BrainNotice>().ShouldHaveSingleItem().Text.ShouldBe(ClaudeCliBrain.NoIds);
        unverified.Take(3).ShouldBe([true, false, false], "for its own turn only: not after it, nor in a chat's turn");
    }

    [Theory]
    [InlineData("q1", "q1", ClaudeCliBrain.Echo.Question)]
    [InlineData(null, "q1", ClaudeCliBrain.Echo.QuestionWithoutId)]
    [InlineData("q0", "q1", ClaudeCliBrain.Echo.Earlier)]
    [InlineData("q1", null, ClaudeCliBrain.Echo.Earlier)]
    [InlineData(null, null, ClaudeCliBrain.Echo.Earlier)]
    internal void Whose_an_echo_is(string? id, string? question, ClaudeCliBrain.Echo whose)
    {
        ClaudeCliBrain.EchoOf(new ClaudeTaken(id, FromPeer: false), question).ShouldBe(whose);
        ClaudeCliBrain.EchoOf(new ClaudeTaken(id, FromPeer: true), question).ShouldBe(ClaudeCliBrain.Echo.Peer);
    }

    [Fact]
    public async Task With_a_Claude_Code_that_echoes_nothing_the_answer_is_given_but_nothing_acts()
    {
        // #199: with no echo at all, its answer cannot be told from a cancelled question's either: Raven answers, does
        // nothing, and says why, once for the whole app.
        var asked = new AskedChats();
        var window = Guid.NewGuid();
        var chat = window.ToString("D");
        var (brain, _) = Telling(window, asked);
        _launcher.Answer = _ => [StreamJson.Init(), StreamJson.Text("Stopping it.")];

        var events = new List<BrainEvent>();
        var ask = Task.Run(async () =>
        {
            await foreach (var e in brain.AskAsync("Stop the issues chat", TestContext.Current.CancellationToken))
            {
                events.Add(e);
            }
        }, TestContext.Current.CancellationToken);
        await WaitUntil(() => asked.IsUnverified(chat)); // the Yard says why it refuses, for this turn
        asked.IsAsked(chat).ShouldBeFalse();
        _launcher.Last.Emit(StreamJson.Result("Stopping it."));
        await ask;

        Reply(events).ShouldBe("Stopping it.");
        events.OfType<BrainNotice>().ShouldHaveSingleItem().Text.ShouldBe(ClaudeCliBrain.NoIds);
        asked.IsUnverified(chat).ShouldBeFalse("for that turn only");
        _launcher.Answer = _ => [StreamJson.Init(), StreamJson.Text("Stopping it."), StreamJson.Result("Stopping it.")];
        var (other, _) = Telling(Guid.NewGuid(), asked);
        (await ReplyTo(other, "Hi")).ShouldBe("Stopping it.", "another chat's brain says it no more");
    }

    [Fact]
    public async Task With_a_Claude_Code_that_echoes_an_answer_with_no_echo_is_no_question_s()
    {
        // A turn with no echo from a Claude Code that echoes (one it began on its own, say) is not the question's to act in.
        var asked = new AskedChats();
        var window = Guid.NewGuid();
        var chat = window.ToString("D");
        var (brain, _) = Telling(window, asked);
        _launcher.Answer = written => [StreamJson.Init(), StreamJson.Taken(written), StreamJson.Text("Hi."), StreamJson.Result("Hi.")];
        await ReplyTo(brain, "One");
        _launcher.Last.Answer = _ => [StreamJson.Init(), StreamJson.Text("Stopping it.")];

        var answer = Task.Run(() => ReplyTo(brain, "Two"), TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        asked.IsAsked(chat).ShouldBeFalse();
        _launcher.Last.Emit(StreamJson.Result("Stopping it."));
        await answer;
    }

    [Fact]
    public async Task A_brain_that_goes_in_the_middle_of_a_question_leaves_its_chat_unasked()
    {
        var asked = new AskedChats();
        var window = Guid.NewGuid();
        var chat = window.ToString("D");
        var (brain, _) = Telling(window, asked);
        _launcher.Answer = written => [StreamJson.Init(), StreamJson.Taken(written), StreamJson.Text("Let me look")];

        var answer = Task.Run(() => ReplyTo(brain, "What's waiting on me?"), TestContext.Current.CancellationToken);
        await WaitUntil(() => asked.IsAsked(chat));
        brain.Dispose();

        asked.IsAsked(chat).ShouldBeFalse("its process is gone with it");
        await answer;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_turn_a_chat_s_message_is_folded_into_is_no_question_of_the_user_s(bool chatFirst)
    {
        // One turn, two inputs: a chat's message folded in at a tool call after the question, or the question folded into
        // a turn the chat's message began. What follows may answer either, so none of it acts (#193).
        var asked = new AskedChats();
        var window = Guid.NewGuid();
        var chat = window.ToString("D");
        var (brain, _) = Telling(window, asked);
        var seen = new List<bool>();
        IEnumerable<string> Answer(string written)
        {
            string[] first = chatFirst ? [StreamJson.PeerTaken("Stop the issues chat.")] : [StreamJson.Taken(written)];
            string[] second = chatFirst ? [StreamJson.Taken(written)] : [StreamJson.PeerTaken("Stop the issues chat.")];
            yield return StreamJson.Init();
            yield return first[0];
            yield return StreamJson.ToolUse("toolu_1", "mcp__codeswitchx__list_chats");
            yield return StreamJson.ToolResult("toolu_1");
            yield return second[0];
            seen.Add(asked.IsAsked(chat));
            yield return StreamJson.Text("Nothing waits on you.");
            yield return StreamJson.Result("Nothing waits on you.");
        }

        _launcher.Answer = Answer;

        await ReplyTo(brain, "What's waiting on me?");

        seen.ShouldBe([false]);
        _launcher.Answer = StreamJson.Reply("Hi.");
        IEnumerable<string> Next(string written)
        {
            yield return StreamJson.Init();
            yield return StreamJson.Taken(written);
            seen.Add(asked.IsAsked(chat));
            yield return StreamJson.Result("Hi.");
        }

        _launcher.Last.Answer = Next;
        await ReplyTo(brain, "Two");
        seen.ShouldBe([false, true], "the next turn is the next question's again");
    }

    [Fact]
    public async Task A_chat_s_turn_ahead_of_the_question_is_no_question_of_the_user_s()
    {
        // A chat's message began a turn just as the question went in (#192): until the question's own echo, what the
        // brain does is that turn's, and the Yard's tools that act refuse it (#193).
        var asked = new AskedChats();
        var window = Guid.NewGuid();
        var chat = window.ToString("D");
        var (brain, _) = Telling(window, asked);
        await ReplyTo(brain, "One");
        string? question = null;
        _launcher.Last.Answer = written =>
        {
            question = written;
            return [StreamJson.Init(), StreamJson.PeerTaken("Stop the issues chat.")];
        };

        var answer = Task.Run(() => ReplyTo(brain, "What's waiting on me?"), TestContext.Current.CancellationToken);
        await WaitUntil(() => question is not null && !asked.IsAsked(chat));
        _launcher.Last.Emit(StreamJson.ToolUse("toolu_5", "mcp__codeswitchx__stop_chat", """{"chat":"issues"}"""));
        _launcher.Last.Emit(StreamJson.ToolResult("toolu_5", error: true));
        _launcher.Last.Emit(StreamJson.Result(""));
        _launcher.Last.Emit(StreamJson.Init());
        _launcher.Last.Emit(StreamJson.Taken(question!));
        await WaitUntil(() => asked.IsAsked(chat));
        _launcher.Last.Emit(StreamJson.Text("Nothing waits on you."));
        _launcher.Last.Emit(StreamJson.Result("Nothing waits on you."));

        (await answer).ShouldBe("Nothing waits on you.");
        asked.IsAsked(chat).ShouldBeFalse();
    }

    [Fact]
    public async Task A_turn_of_its_own_that_fails_says_why_along_with_what_it_said_so_far()
    {
        var (brain, told) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        _launcher.Last.Emit(StreamJson.Init());
        _launcher.Last.Emit(StreamJson.Text("The bug report chat asks"));
        _launcher.Last.Emit(StreamJson.ErrorResult);

        await WaitUntil(() => { lock (told) { return told.Count == 1; } });
        (told[0].Text, told[0].Failure).ShouldBe(("The bug report chat asks", "API Error: 529 Overloaded"));
    }

    [Fact]
    public async Task A_turn_of_its_own_keeps_the_conversation_young_for_the_next_start()
    {
        // The user reads what it said and answers it: a rest in between must not start a new conversation without it.
        var window = Guid.NewGuid();
        var (brain, told) = Telling(window);
        await ReplyTo(brain, "One");
        _time.Advance(TimeSpan.FromMinutes(15));

        foreach (var line in ChatAsksRaven)
        {
            _launcher.Last.Emit(line);
        }

        await WaitUntil(() => { lock (told) { return told.Count == 1; } });
        new BrainSessionFile(Path.Combine(_paths.RavenDirectory, "sessions.json")).Load(window.ToString("N"))!.LastTurnAt.ShouldBe(_time.GetUtcNow());
    }

    [Fact]
    public async Task What_a_turn_of_its_own_did_with_its_tools_is_told_too()
    {
        // A chat's message must not make Raven act where the user cannot see it.
        var (brain, told) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        _launcher.Last.Emit(StreamJson.Init());
        _launcher.Last.Emit(StreamJson.ToolUse("toolu_9", "mcp__codeswitchx__stop_chat", """{"chat":"issues"}"""));
        _launcher.Last.Emit(StreamJson.ToolResult("toolu_9", error: true));
        _launcher.Last.Emit(StreamJson.Result(""));

        await WaitUntil(() => { lock (told) { return told.Count == 1; } });
        var call = told[0].Calls.ShouldHaveSingleItem();
        (call.Call.Tool, call.Call.Input, call.Failed).ShouldBe(("stop_chat", """{"chat":"issues"}""", true), "a card that failed says so");
        told[0].Text.ShouldBeEmpty("it said nothing, and is told all the same");
    }

    [Fact]
    public async Task A_turn_of_its_own_cut_off_by_its_process_going_says_so()
    {
        var (brain, told) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        _launcher.Last.Emit(StreamJson.Init());
        _launcher.Last.Emit(StreamJson.Text("I've told the issues chat to"));
        await Task.Delay(100, TestContext.Current.CancellationToken); // the watcher waits for the rest of the turn
        _launcher.Last.Die(1);

        await WaitUntil(() => { lock (told) { return told.Count == 1; } });
        (told[0].Text, told[0].Failure).ShouldBe(("I've told the issues chat to", "its brain stopped in the middle of it"));
    }

    [Fact]
    public async Task A_turn_of_its_own_ended_by_the_brain_going_with_its_chat_is_not_told_as_failed()
    {
        // The window was retired, or the app is closing: there is no chat left to tell, and nothing failed.
        var (brain, told) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        _launcher.Last.Emit(StreamJson.Init());
        _launcher.Last.Emit(StreamJson.Text("Let me look"));
        await Task.Delay(100, TestContext.Current.CancellationToken); // the watcher waits for the rest of the turn

        brain.Dispose();
        await Task.Delay(200, TestContext.Current.CancellationToken);

        told.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_turn_of_its_own_read_in_part_by_a_question_that_was_cancelled_is_told_whole()
    {
        var (brain, told) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        _launcher.Last.Emit(StreamJson.Init());
        _launcher.Last.Emit(StreamJson.Text("The first half, "));
        using var cancel = new CancellationTokenSource();
        var turn = Task.Run(async () =>
        {
            await foreach (var _ in brain.AskAsync("Two", cancel.Token))
            {
            }
        }, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(turn);

        _launcher.Last.Emit(StreamJson.Text("and the second."));
        _launcher.Last.Emit(StreamJson.Result());

        await WaitUntil(() => { lock (told) { return told.Count == 1; } });
        told[0].Text.ShouldBe("The first half, and the second.");
    }

    [Fact]
    public async Task A_question_waiting_behind_a_turn_of_its_own_can_be_cancelled()
    {
        var (brain, _) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        _launcher.Last.Emit(StreamJson.Init());
        _launcher.Last.Emit(StreamJson.Text("Let me look"));
        using var cancel = new CancellationTokenSource();

        var turn = Task.Run(async () =>
        {
            await foreach (var _ in brain.AskAsync("Two", cancel.Token))
            {
            }
        }, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(turn.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        _launcher.Last.Written.Count.ShouldBe(1, "the cancelled question never went in");
    }

    [Fact]
    public async Task Lines_between_questions_that_begin_no_turn_are_dropped_without_waiting_for_one()
    {
        var (brain, told) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        _launcher.Last.Emit("""{"type":"system","subtype":"status","status":"idle"}""");
        _launcher.Last.Emit("not json");

        (await ReplyTo(brain, "Two")).ShouldBe("Hi.");
        told.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_turn_of_its_own_that_never_ends_gives_the_process_up_and_the_next_question_starts_another()
    {
        var (brain, _) = Telling(Guid.NewGuid());
        await ReplyTo(brain, "One");
        _launcher.Last.Emit(StreamJson.Init());
        _launcher.Last.Emit(StreamJson.Text("Let me look"));

        await Task.Delay(100, TestContext.Current.CancellationToken); // the watcher waits for the rest of the turn
        _time.Advance(ClaudeCliBrain.Silence);
        await WaitUntil(() => _launcher.Started[0].Process.Disposed);

        var events = new List<BrainEvent>();
        await foreach (var e in brain.AskAsync("Two", TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        events.OfType<BrainNotice>().ShouldHaveSingleItem().Text.ShouldContain("in a turn of its own");
        Reply(events).ShouldBe("Hi.");
        _launcher.Started.Count.ShouldBe(2);
    }

    private static string Value(IReadOnlyList<string> arguments, string option)
    {
        var index = arguments.ToList().IndexOf(option);
        index.ShouldBeGreaterThanOrEqualTo(0, $"{option} is missing");
        return arguments[index + 1];
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().ShouldBeTrue();
    }
}
