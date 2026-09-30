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
        // A chat that answers: its mode, then its first words.
        _launcher.Answer = _ => [StreamJson.Init(mode: "auto"), StreamJson.AssistantText("On it.")];
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
        _launcher.Answer = _ => [StreamJson.Init(mode: "auto"), StreamJson.ErrorResult];

        var start = await StartAsync();

        start.Failure.ShouldBe("The chat could not start: API Error: 529 Overloaded");
        _launcher.Last.InputClosed.ShouldBeTrue();
        _agents.Chats.ShouldBeEmpty();
        _failures.ShouldBeEmpty(); // the start says it
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

        _time.Advance(ClaudeAgentLauncher.StartWait);

        (await start).Failure.ShouldBeNull();
        _agents.Chats.ShouldHaveSingleItem();
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

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().ShouldBeTrue();
    }
}
