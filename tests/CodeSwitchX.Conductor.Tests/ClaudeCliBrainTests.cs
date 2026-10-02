using System.ComponentModel;
using System.Text.Json;
using CodeSwitchX.Core;
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
