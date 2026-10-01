using System.Text.Json;
using CodeSwitchX.Core.Yard;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.Conductor.Tests;

public sealed class ClaudeAgentLauncherTests : IDisposable
{
    private const string Claude = @"C:\Tools\claude.exe";
    private readonly string _folder = Directory.CreateTempSubdirectory("csx-agent-").FullName;
    private readonly FakeLauncher _launcher = new();
    private readonly FakeTimeProvider _time = new();
    private readonly List<AgentChat> _changes = [];
    private readonly List<(AgentChat Chat, string Why)> _failures = [];
    private string? _claude = Claude;
    private readonly ClaudeAgentLauncher _agents;

    public ClaudeAgentLauncherTests()
    {
        // A chat that answers: its mode, the line it takes, then its first words.
        _launcher.Answer = line => [StreamJson.Init(mode: "auto"), StreamJson.Taken(line), StreamJson.AssistantText("On it.")];
        _agents = new ClaudeAgentLauncher(_launcher, () => _claude, _time, NullLogger<ClaudeAgentLauncher>.Instance);
        _agents.Changed += c =>
        {
            lock (_changes)
            {
                _changes.Add(c);
            }
        };
        _agents.Failed += (c, why) =>
        {
            lock (_failures)
            {
                _failures.Add((c, why));
            }
        };
    }

    public void Dispose()
    {
        _agents.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private AgentRequest Request(string? model = "claude-fable-5-1", string? effort = "high", string id = "11111111-aaaa-bbbb-cccc-000000000001") =>
        new(id, Guid.Empty, "Diffusion-Full", _folder, "Filter the LoRA list by base model.", model, effort);

    private Task<AgentStart> StartAsync(AgentRequest? request = null) => _agents.StartAsync(request ?? Request(), TestContext.Current.CancellationToken);

    private static string? Value(IReadOnlyList<string> arguments, string name)
    {
        var at = arguments.ToList().IndexOf(name);
        return at < 0 ? null : arguments[at + 1];
    }

    [Fact]
    public async Task A_chat_runs_in_its_folder_in_auto_mode_with_its_own_id_model_and_effort()
    {
        await StartAsync();

        var (executable, arguments, folder, _) = _launcher.Started.ShouldHaveSingleItem();
        executable.ShouldBe(Claude);
        folder.ShouldBe(_folder);
        arguments.Take(6).ShouldBe(["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose"]);
        arguments.ShouldContain("--replay-user-messages");
        Value(arguments, "--permission-mode").ShouldBe("auto");
        Value(arguments, "--session-id").ShouldBe("11111111-aaaa-bbbb-cccc-000000000001");
        Value(arguments, "--model").ShouldBe("claude-fable-5-1");
        Value(arguments, "--effort").ShouldBe("high");
    }

    [Fact]
    public async Task It_is_marked_as_VS_Code_s_so_the_extension_opens_it_later()
    {
        await StartAsync();

        _launcher.Environments.ShouldHaveSingleItem().ShouldNotBeNull()["CLAUDE_CODE_ENTRYPOINT"].ShouldBe("claude-vscode");
    }

    [Fact]
    public async Task A_model_and_effort_left_out_are_left_to_Claude_Code()
    {
        await StartAsync(Request(model: null, effort: null));

        var arguments = _launcher.Started.ShouldHaveSingleItem().Arguments;
        arguments.ShouldNotContain("--model");
        arguments.ShouldNotContain("--effort");
    }

    [Fact]
    public async Task The_prompt_is_its_first_turn()
    {
        await StartAsync();

        using var line = JsonDocument.Parse(_launcher.Last.Written.ShouldHaveSingleItem());
        line.RootElement.GetProperty("type").GetString().ShouldBe("user");
        line.RootElement.GetProperty("message").GetProperty("content").GetString().ShouldBe("Filter the LoRA list by base model.");
    }

    [Fact]
    public async Task A_start_returns_once_the_chat_answers_working_and_in_the_mode_it_reported()
    {
        var start = await StartAsync();

        start.Failure.ShouldBeNull();
        start.Chat.Working.ShouldBeTrue();
        start.Chat.PermissionMode.ShouldBe("auto");
        _agents.Chats.ShouldHaveSingleItem().Id.ShouldBe("11111111-aaaa-bbbb-cccc-000000000001");
    }

    [Fact]
    public async Task A_model_it_refuses_fails_the_start_and_the_chat_is_gone()
    {
        _launcher.Answer = line => [StreamJson.Init(mode: "auto"), StreamJson.Taken(line), StreamJson.ErrorResult];

        var start = await StartAsync();

        start.Failure.ShouldBe("The chat could not start: API Error: 529 Overloaded");
        _launcher.Last.InputClosed.ShouldBeTrue();
        _agents.Chats.ShouldBeEmpty();
        _failures.ShouldBeEmpty(); // the start says it
    }

    [Fact]
    public async Task A_model_that_does_not_exist_fails_the_start_though_Claude_Code_answers_for_it()
    {
        // An alias table edited to a wrong id: Claude Code writes an assistant message of its own, then the failed result.
        _launcher.Answer = line => [StreamJson.Init(mode: "auto"), StreamJson.Taken(line), StreamJson.ModelNotFound, StreamJson.ModelNotFoundResult];

        var start = await StartAsync();

        start.Failure.ShouldBe("The chat could not start: There's an issue with the selected model (claude-opus-5-6).");
        _agents.Chats.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_turn_that_fails_right_after_the_chat_began_is_reported()
    {
        // The failure is read before the start has returned: it is news all the same, as the start says "started".
        _launcher.Answer = line => [StreamJson.Init(mode: "auto"), StreamJson.Taken(line), StreamJson.AssistantText("On it."), StreamJson.ErrorResult];

        (await StartAsync()).Failure.ShouldBeNull();

        await WaitUntil(() => _failures.Count == 1);
        _failures[0].Why.ShouldBe("API Error: 529 Overloaded");
    }

    [Fact]
    public async Task A_claude_that_ends_before_the_chat_began_fails_the_start()
    {
        _launcher.Answer = _ => [];
        var start = StartAsync();
        await WaitUntil(() => _launcher.Started.Count == 1 && _launcher.Last.Written.Count == 1);

        _launcher.Last.Die(1);

        (await start).Failure.ShouldBe("Claude Code stopped (exit code 1) before the chat began.");
    }

    [Fact]
    public async Task A_chat_that_thinks_long_before_its_first_word_counts_as_started()
    {
        _launcher.Answer = _ => [StreamJson.Init(mode: "auto")];
        var start = StartAsync();
        await WaitUntil(() => _launcher.Started.Count == 1 && _launcher.Last.Written.Count == 1);

        // Again until it is over: the start sets its timer only after the prompt is written.
        for (var i = 0; i < 200 && !start.IsCompleted; i++)
        {
            _time.Advance(ClaudeAgentLauncher.StartWait);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        (await start).Failure.ShouldBeNull();
        _agents.Chats.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_start_the_caller_gives_up_on_stops_the_chat()
    {
        _launcher.Answer = _ => [StreamJson.Init(mode: "auto")]; // thinking, no word yet
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var start = _agents.StartAsync(Request(), cancel.Token);
        await WaitUntil(() => _launcher.Started.Count == 1 && _launcher.Last.Written.Count == 1);

        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => start);
        _launcher.Last.Disposed.ShouldBeTrue();
        _agents.Chats.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_claude_that_exits_while_its_start_is_told_of_is_last_told_of_as_ended()
    {
        // claude exits straight after launch (an auth error), while the start's own news is on its way: that older news
        // must not arrive after the end, or the row would show a chat no process runs.
        List<AgentChat> told = [];
        _agents.Changed += c =>
        {
            if (told.Count == 0 && !c.Ended)
            {
                _launcher.Last.Die(1);
                Thread.Sleep(200); // the pump reads the end meanwhile
            }

            lock (told)
            {
                told.Add(c);
            }
        };

        (await StartAsync()).Failure.ShouldNotBeNull();

        await WaitUntil(() => told.Any(c => c.Ended));
        lock (told)
        {
            told[^1].Ended.ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task No_Claude_Code_or_no_folder_is_said_before_anything_starts(bool claudeMissing)
    {
        _claude = claudeMissing ? null : Claude;
        var request = claudeMissing ? Request() : Request() with { Folder = Path.Combine(_folder, "gone") };

        var error = await Should.ThrowAsync<YardActionException>(() => StartAsync(request));

        error.Message.ShouldContain(claudeMissing ? "Claude Code is not installed" : "is not there any more");
        _launcher.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_end_of_a_turn_is_seen()
    {
        await StartAsync();

        _launcher.Last.Emit(StreamJson.Result("Done."));

        await WaitUntil(() => !_agents.Chats.Single().Working);
        _changes.ShouldContain(c => !c.Working);
    }

    [Fact]
    public async Task What_is_sent_is_its_next_turn()
    {
        await StartAsync();
        _launcher.Last.Emit(StreamJson.Result("Done."));
        await WaitUntil(() => !_agents.Chats.Single().Working);

        var chat = await _agents.SendAsync("11111111", "Add tests too.", TestContext.Current.CancellationToken);

        chat.Working.ShouldBeTrue();
        using var line = JsonDocument.Parse(_launcher.Last.Written[^1]);
        line.RootElement.GetProperty("message").GetProperty("content").GetString().ShouldBe("Add tests too.");
    }

    [Fact]
    public async Task A_turn_sent_while_one_runs_keeps_the_chat_working_until_it_is_over_too()
    {
        await StartAsync();
        _launcher.Last.Answer = _ => []; // queued: taken once the running turn is over
        await _agents.SendAsync("11111111", "Add tests too.", TestContext.Current.CancellationToken);

        await ReadAsync(StreamJson.Result("Done."));

        _agents.Chats.Single().Working.ShouldBeTrue();
        await ReadAsync(StreamJson.Init(mode: "auto"));
        _launcher.Last.Emit(StreamJson.Taken(_launcher.Last.Written[^1]));
        _launcher.Last.Emit(StreamJson.Result("Tests added."));
        await WaitUntil(() => !_agents.Chats.Single().Working);
    }

    [Fact]
    public async Task A_line_sent_during_a_tool_call_is_folded_into_that_turn_and_its_end_leaves_the_chat_idle()
    {
        // Seen with CLI 2.1.286: two lines, one result.
        await StartAsync();
        _launcher.Last.Answer = _ => [];
        await _agents.SendAsync("11111111", "Add tests too.", TestContext.Current.CancellationToken);

        _launcher.Last.Emit(StreamJson.Taken(_launcher.Last.Written[^1]));
        await ReadAsync(StreamJson.Result("Done, tests too."));

        _agents.Chats.Single().Working.ShouldBeFalse();
    }

    [Fact]
    public async Task A_turn_Claude_Code_starts_by_itself_is_work_until_it_is_over()
    {
        // A background task that ends starts a turn with no line sent: its init, then its result.
        await StartAsync();
        await ReadAsync(StreamJson.Result("Started the build in the background."));
        _agents.Chats.Single().Working.ShouldBeFalse();

        await ReadAsync(StreamJson.Init(mode: "auto"));
        _agents.Chats.Single().Working.ShouldBeTrue();

        await ReadAsync(StreamJson.Result("The build passed."));
        _agents.Chats.Single().Working.ShouldBeFalse();
    }

    [Fact]
    public async Task A_Claude_Code_that_does_not_echo_its_lines_is_idle_once_its_turn_is_over()
    {
        _launcher.Answer = _ => [StreamJson.Init(mode: "auto"), StreamJson.AssistantText("On it.")];
        await StartAsync();

        await ReadAsync(StreamJson.Result("Done."));

        _agents.Chats.Single().Working.ShouldBeFalse();
    }

    [Fact]
    public async Task A_send_given_up_on_leaves_an_idle_chat_idle()
    {
        await StartAsync();
        await ReadAsync(StreamJson.Result("Done."));

        await Should.ThrowAsync<OperationCanceledException>(() => _agents.SendAsync("11111111", "Add tests too.", new CancellationToken(canceled: true)));

        _agents.Chats.Single().Working.ShouldBeFalse();
    }

    [Fact]
    public async Task Only_a_chat_it_runs_can_be_sent_to_or_stopped()
    {
        await Should.ThrowAsync<YardActionException>(() => _agents.SendAsync("nope", "Hi", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<YardActionException>(() => _agents.StopAsync("nope", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_chat_between_turns_is_stopped_by_closing_its_input()
    {
        await StartAsync();
        _launcher.Last.Emit(StreamJson.Result("Done."));
        await WaitUntil(() => !_agents.Chats.Single().Working);

        var before = await _agents.StopAsync("11111111-aaaa-bbbb-cccc-000000000001", TestContext.Current.CancellationToken);

        before.Working.ShouldBeFalse();
        _launcher.Last.InputClosed.ShouldBeTrue();
        _agents.Chats.ShouldBeEmpty();
        _changes[^1].Ended.ShouldBeTrue();
        _changes[^1].Stopped.ShouldBeTrue("the app stopped it: its end is no failure");
        _failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_chat_in_the_middle_of_a_turn_is_killed()
    {
        await StartAsync();

        var before = await _agents.StopAsync("11111111-aaaa-bbbb-cccc-000000000001", TestContext.Current.CancellationToken);

        before.Working.ShouldBeTrue();
        _launcher.Last.InputClosed.ShouldBeFalse();
        _launcher.Last.Disposed.ShouldBeTrue();
        _agents.Chats.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_started_chat_that_dies_is_reported()
    {
        await StartAsync();

        _launcher.Last.Die(3);

        await WaitUntil(() => _failures.Count == 1);
        _failures[0].Why.ShouldBe("Claude Code stopped (exit code 3).");
        _agents.Chats.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_later_turn_that_fails_is_reported_and_the_chat_goes_on()
    {
        await StartAsync();

        _launcher.Last.Emit(StreamJson.ErrorResult);

        await WaitUntil(() => _failures.Count == 1);
        _failures[0].Why.ShouldBe("API Error: 529 Overloaded");
        _agents.Chats.ShouldHaveSingleItem().Working.ShouldBeFalse();
    }

    [Fact]
    public async Task A_chat_is_found_by_the_start_of_its_id_when_that_fits_one()
    {
        await StartAsync();
        await StartAsync(Request(id: "11111111-aaaa-bbbb-cccc-000000000002"));

        _agents.Find("11111111-aaaa-bbbb-cccc-000000000002").ShouldNotBeNull();
        _agents.Find("11111111").ShouldBeNull(); // fits both
        _agents.Find("").ShouldBeNull();
    }

    [Fact]
    public async Task Closing_the_app_kills_every_chat_and_starts_none_after()
    {
        await StartAsync();

        _agents.Dispose();

        _launcher.Last.Disposed.ShouldBeTrue();
        await Should.ThrowAsync<YardActionException>(() => StartAsync(Request(id: "22222222")));
        _failures.ShouldBeEmpty();
    }

    /// <summary>The chat writes the line; returns once it is read, which tells of the chat.</summary>
    private async Task ReadAsync(string line)
    {
        int told;
        lock (_changes)
        {
            told = _changes.Count;
        }

        _launcher.Last.Emit(line);
        await WaitUntil(() =>
        {
            lock (_changes)
            {
                return _changes.Count > told;
            }
        });
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
